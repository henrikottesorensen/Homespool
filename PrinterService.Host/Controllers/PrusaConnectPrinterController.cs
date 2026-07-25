using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO;

namespace PrinterService.Host.Controllers;

[Authorize(Authorization.Policies.PrusaConnectPrinter)]
[ApiController]
public class PrusaConnectPrinterController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly WebSocketHandler _webSocketHandler;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly IPrinterCommandCorrelator _correlator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PrusaConnectPrinterController> _logger;

    public PrusaConnectPrinterController(PrusaConnectService prusaConnectService,
                                         WebSocketHandler webSocketHandler,
                                         PrinterConnectionRegistry connectionRegistry,
                                         IPrinterCommandCorrelator correlator,
                                         IHostApplicationLifetime lifetime,
                                         ILogger<PrusaConnectPrinterController> logger)
    {
        _prusaConnectService = prusaConnectService;
        _webSocketHandler = webSocketHandler;
        _connectionRegistry = connectionRegistry;
        _correlator = correlator;
        _lifetime = lifetime;
        _logger = logger;
    }
    
    
    [Route("/p/ws")]
    public async Task<ActionResult> ConnectWebSocket()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                // Guaranteed present: [Authorize] above only lets the request through once
                // PrusaConnectPrinterAuthenticationHandler has already resolved the Fingerprint
                // header to a Printer and issued this claim.
                int printerId = int.Parse(User.FindFirstValue(PsClaimTypes.PrinterId)!);

                using WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    SubProtocol = Headers.Values.WSProtocolPrusaConnect,
                    KeepAliveInterval = TimeSpan.FromSeconds(120),
                    KeepAliveTimeout = TimeSpan.FromSeconds(120),
                });

                _logger.LogDebug("Connected websocket from {Client}:{Port} {Printer} {Fingerprint} {PrinterId}",
                    HttpContext.Connection.RemoteIpAddress,
                    HttpContext.Connection.RemotePort,
                    clientHeaders.Printer,
                    clientHeaders.FingerPrint,
                    printerId);

                // The read side: WebSocketStream (.NET 10) presents the socket as a plain byte
                // stream - EOF at the peer's close frame - and StreamPipeReader gives the handler
                // the PipeReader it parses from. Reads pull straight from the socket, so there is
                // no pump task to run alongside the handler. Writes don't go through the stream at
                // all (WebSocketPrinterConnection sends on the socket directly), so the message
                // type here is inert; ownsWebSocket stays false because the close handshake is
                // handled explicitly below.
                using Stream socketStream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary);
                PipeReader input = PipeReader.Create(socketStream, new StreamPipeReaderOptions(leaveOpen: true));

                WebSocketPrinterConnection connection = new(webSocket);
                _connectionRegistry.Register(printerId, connection);

                // Two reasons this read loop should stop, neither of them the printer's doing:
                // RequestAborted (the client vanished, or Kestrel aborted us) and ApplicationStopping.
                // The second is the load-bearing one: a printer connection is idle-but-open for hours,
                // so without it every shutdown parked here until Kestrel's timeout expired and killed
                // the request - a ~30s stall that invites operators to SIGKILL instead, which is how
                // buffered telemetry actually gets lost. Cancelling the receive aborts the socket, so
                // the printer sees the connection drop and reconnects on its own retry timer.
                using CancellationTokenSource connectionLifetime =
                    CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _lifetime.ApplicationStopping);

                // Which status the close frame carries. Recorded rather than sent inline, so every
                // exit closes in one place - after the connection has left the registry.
                //
                // NormalClosure covers both ways the handler returns without throwing: the printer
                // closed its end (EOF on the reader), and cancellation landing between reads rather
                // than inside one, where the loop condition ends it. The latter reaches here with the
                // socket still open, so shutdown sends a real close frame when it wins that race and
                // aborts the socket when it doesn't - both acceptable ends, and the printer
                // reconnects from either.
                WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;

                try
                {
                    await _webSocketHandler.HandlePrusaWebsocket(input, printerId, connectionLifetime.Token);
                }
                catch (JsonException)
                {
                    // The handler's contract: malformed JSON is a protocol violation. Close on it,
                    // then let it surface - same as when the handler owned the close itself.
                    closeStatus = WebSocketCloseStatus.PolicyViolation;
                    throw;
                }
                catch (WebSocketException e)
                {
                    // Routine, not exceptional: printers drop off the network without a close
                    // handshake all the time. The socket is already gone - nothing to close.
                    _logger.LogDebug(e, "[{PrinterId}] connection closed abruptly", printerId);
                }
                catch (OperationCanceledException e)
                {
                    // Shutdown or teardown, per connectionLifetime above - including Kestrel aborting
                    // the connection outright, since ConnectionAbortedException derives from this.
                    // Ordinary ends to a WebSocket request, not faults: left unhandled they surfaced
                    // as a logged 500.
                    _logger.LogDebug(e, "[{PrinterId}] connection ended: shutting down or aborted", printerId);
                }
                finally
                {
                    await input.CompleteAsync();

                    // Unregister before closing, not after: while the connection is still in the
                    // registry a command can pass its IsOpen check and start writing to this socket,
                    // and the close frame is a write too. Removing it first bounds that race to
                    // callers already holding a reference, which WebSocketPrinterConnection's write
                    // lock then serializes against the close itself.
                    _connectionRegistry.Unregister(printerId, connection);

                    // If a command was sent and is still awaiting a reply when the socket goes away,
                    // fail it immediately rather than making the caller wait out the response timeout
                    // for an answer that will now never arrive.
                    _correlator.Cancel(printerId);

                    await connection.CloseOutputAsync(closeStatus);
                }

                // Not Ok(): the response started at the 101, and a status-code result sets
                // Response.StatusCode during result execution - after this action returns, outside
                // the try above - which throws "the response has already started" and surfaces as an
                // unhandled error the client never sees, because the socket is closed by then.
                return new EmptyResult();
            }
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return BadRequest();
        }

        return BadRequest();
    }

    [AllowAnonymous]
    [HttpPost]
    [Route("/p/register")]
    public async Task<ActionResult<string>> RegisterPrinter([FromBody] RegisterPrinterRequestDTO printer)
    {
        try
        {
            // [FromBody] is required. Without it - and without [ApiController], which is deliberately
            // not used here (see ApiExplorerVisibilityConvention) - MVC binds complex parameters from
            // form data, not the JSON body, leaving every property null. The insert then died on
            // NOT NULL SerialNumber. Everything this action needs is in the body; the printer sends
            // no headers at all on this request.

            // Get code for printer.
            CodeResponseDTO code = await _prusaConnectService.GetPrinterCode(printer);

            // Apparently we return all the data in headers?
            Response.Headers.TryAdd(Headers.Code, code.TemporaryCode);
            Response.Headers.TryAdd(Headers.TemporaryCode, code.TemporaryCode);

            Response.Headers.TryAdd(Headers.Expires, $"{code.Expires:R}");

            return Content(string.Empty, MediaTypeNames.Text.Html);
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return BadRequest();
        }
    }
    
    [AllowAnonymous]
    [HttpGet]
    [Route("/p/register")]
    public async Task<ActionResult<string>> GetPrinterRegistrationStatus()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            // The printer identifies itself by the code alone here: Buddy's poll carries a Code
            // header and nothing else.
            if (clientHeaders.Code is null)
            {
                return BadRequest("Code missing");
            }

            string? token = await _prusaConnectService.GetToken(clientHeaders.Code);

            if (string.IsNullOrWhiteSpace(token))
            {
                return Accepted(new MessageDTO
                {
                    Message = "User hasn't used Temporary-Code yet. Printer must call it one more time",
                    Code = "REGISTRATION_ACCEPTED",
                });
            }
            
            Response.Headers.TryAdd(Headers.Token, token);
            return Ok();
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return BadRequest();
        }
        catch (PrinterNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
