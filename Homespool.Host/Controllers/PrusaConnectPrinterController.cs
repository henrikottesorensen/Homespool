using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;

namespace Homespool.Host.Controllers;

[ApiController]
[Authorize(Authorisation.Policies.PrusaConnectPrinter)]
public class PrusaConnectPrinterController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly PrinterConnectionSession _session;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PrusaConnectPrinterController> _logger;

    public PrusaConnectPrinterController(PrusaConnectService prusaConnectService,
                                         PrinterConnectionSession session,
                                         IHostApplicationLifetime lifetime,
                                         ILogger<PrusaConnectPrinterController> logger)
    {
        _prusaConnectService = prusaConnectService;
        _session = session;
        _lifetime = lifetime;
        _logger = logger;
    }

    // [HttpGet] as well as the route, so this reaches the OpenAPI document at all: ApiExplorer cannot
    // describe an action with no method constraint, which is why /p/ws was the one printer endpoint
    // missing from it. A WebSocket upgrade is a GET, so this also narrows the routing to what the
    // protocol actually uses (Henrik, 2026-08-01, after the change was flagged as touching /p/*).
    [HttpGet]
    [Route("/p/ws")]
    [EnableRateLimiting(Program.PrinterSocketRateLimitPolicy)]

    // The 101 is said here because nothing in the union can: the response starts inside the action.
    [ProducesResponseType(typeof(void), StatusCodes.Status101SwitchingProtocols)]
    public async Task<Results<EmptyHttpResult, BadRequest>> ConnectWebSocket()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                // Guaranteed present: [Authorize] above only lets the request through once
                // PrusaConnectPrinterAuthenticationHandler has already resolved the Fingerprint
                // header to a Printer and issued this claim.
                int printerId = int.Parse(User.FindFirstValue(HSClaimTypes.PrinterId)!);

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
                // type here is inert; ownsWebSocket stays false because the close handshake is the
                // session's, below.
                await using Stream socketStream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary);
                PipeReader input = PipeReader.Create(socketStream, new StreamPipeReaderOptions(leaveOpen: true));

                // No transport argument any more. It used to take Request.IsHttps to size frames for
                // the printer's TLS record buffer; nginx terminates that TLS now and OpenSSL honours
                // the max_fragment_length the printer negotiates, so this socket is plain HTTP to the
                // proxy and record size is not this process's business. See WebSocketPrinterConnection
                // .MaxFramePayload - including why putting the old sizing back would hide a real
                // failure rather than fix one.
                WebSocketPrinterConnection connection = new(webSocket);

                // Two reasons this read loop should stop, neither of them the printer's doing:
                // RequestAborted (the client vanished, or Kestrel aborted us) and ApplicationStopping.
                // The second is the load-bearing one: a printer connection is idle-but-open for hours,
                // so without it every shutdown parked here until Kestrel's timeout expired and killed
                // the request - a ~30s stall that invites operators to SIGKILL instead, which is how
                // buffered telemetry actually gets lost. Cancelling the receive aborts the socket, so
                // the printer sees the connection drop and reconnects on its own retry timer.
                using CancellationTokenSource connectionLifetime =
                    CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _lifetime.ApplicationStopping);

                // Everything from here - register, read loop, ordered teardown, close - belongs to
                // the session, which owns `input` from this point and completes it. It is a separate
                // type purely so that sequence is reachable without an HttpContext, and so a test
                // can pin the order each of its steps was bought with.
                await _session.RunAsync(printerId, connection, input, connectionLifetime.Token);

                // Not Ok(): the response started at the 101, and a status-code result sets
                // Response.StatusCode during result execution - after this action returns, outside
                // the try above - which throws "the response has already started" and surfaces as an
                // unhandled error the client never sees, because the socket is closed by then.
                return TypedResults.Empty;
            }
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.BadRequest();
    }

    [AllowAnonymous]
    [EnableRateLimiting(Program.PrinterRegistrationRateLimitPolicy)]
    [HttpPost]
    [Route("/p/register")]

    // Firmware reads the status code and the Code header; the body is deliberately empty, and
    // text/html rather than JSON because that is what Connect answers with. The 200 is said here
    // because a content result carries no metadata of its own.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    public async Task<Results<ContentHttpResult, BadRequest>> RegisterPrinter([FromBody] RegisterPrinterRequestDTO printer)
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

            return TypedResults.Content(string.Empty, MediaTypeNames.Text.Html);
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }
    }

    [AllowAnonymous]
    [EnableRateLimiting(Program.PrinterRegistrationRateLimitPolicy)]
    [HttpGet]
    [Route("/p/register")]

    // 200 carries the token in a header and nothing in the body; 202 is the one answer here with a
    // payload, telling the printer to poll again. The 401 is said here because an unauthorized
    // result carries no metadata of its own; the rest say theirs through the union.
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    public async Task<Results<Ok, Accepted<MessageDTO>, ContentHttpResult, BadRequest, NotFound, UnauthorizedHttpResult>>
        GetPrinterRegistrationStatus()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            // The printer identifies itself by the code alone here: Buddy's poll carries a Code
            // header and nothing else.
            if (clientHeaders.Code is null)
            {
                return TypedResults.Text("Code missing", statusCode: StatusCodes.Status400BadRequest);
            }

            string? token = await _prusaConnectService.GetToken(clientHeaders.Code);

            if (string.IsNullOrWhiteSpace(token))
            {
                return TypedResults.Accepted((string?)null, new MessageDTO
                {
                    Message = "User hasn't used Temporary-Code yet. Printer must call it one more time",
                    Code = "REGISTRATION_ACCEPTED",
                });
            }

            Response.Headers.TryAdd(Headers.Token, token);
            return TypedResults.Ok();
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }
        catch (PrinterNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }
}
