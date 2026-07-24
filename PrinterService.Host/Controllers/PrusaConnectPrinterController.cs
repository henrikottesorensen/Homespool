using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private readonly ILogger<PrusaConnectPrinterController> _logger;

    public PrusaConnectPrinterController(PrusaConnectService prusaConnectService,
                                         WebSocketHandler webSocketHandler,
                                         PrinterConnectionRegistry connectionRegistry,
                                         ILogger<PrusaConnectPrinterController> logger)
    {
        _prusaConnectService = prusaConnectService;
        _webSocketHandler = webSocketHandler;
        _connectionRegistry = connectionRegistry;
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

                using IWebSocketPipe pipe = webSocket.CreatePipe(true);

                WebSocketPrinterConnection connection = new(pipe);
                _connectionRegistry.Register(printerId, connection);

                try
                {
                    await Task.WhenAll(_webSocketHandler.HandlePrusaWebsocket(pipe, printerId, CancellationToken.None), pipe.RunAsync());
                }
                finally
                {
                    _connectionRegistry.Unregister(printerId, connection);
                }

                return Ok();
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
