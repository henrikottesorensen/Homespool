using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using PrinterService.Api.Exceptions;
using PrinterService.Api.PrusaConnect;
using PrinterService.Api.PrusaConnect.DTO;

namespace PrinterService.Api.Controllers;                          

// [Authorize(Authorization.Policies.PrusaConnectPrinter)]
public class PrusaConnectPrinterController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly WebSocketHandler _webSocketHandler;
    private readonly ILogger<PrusaConnectPrinterController> _logger;

    public PrusaConnectPrinterController(PrusaConnectService prusaConnectService,
                                         WebSocketHandler webSocketHandler,
                                         ILogger<PrusaConnectPrinterController> logger)
    {
        _prusaConnectService = prusaConnectService;
        _webSocketHandler = webSocketHandler;
        _logger = logger;
    }
    
    
    [Route("/p/ws")]
    public async Task<ActionResult> ConnectWebSocket()
    {
        try
        {
            //PrinterClientHeaders clientHeaders = new(Request);
            
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                using WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    SubProtocol = Headers.Values.WSProtocolPrusaConnect,
                    KeepAliveInterval = TimeSpan.FromSeconds(120),
                    KeepAliveTimeout = TimeSpan.FromSeconds(120),
                });

                // _logger.LogDebug("Connected websocket from {Client}:{Port} {Printer} {Fingerprint}",
                //     HttpContext.Connection.RemoteIpAddress,
                //     HttpContext.Connection.RemotePort,
                //     clientHeaders.Printer,
                //     clientHeaders.FingerPrint);
                
                using IWebSocketPipe pipe = webSocket.CreatePipe(true);

                await Task.WhenAll(_webSocketHandler.HandlePrusaWebsocket(pipe, CancellationToken.None), pipe.RunAsync());

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
    public async Task<ActionResult<string>> RegisterPrinter(RegisterPrinterRequestDTO printer)
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);
            
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

            if (clientHeaders.TemporaryCode is null)
            {
                return BadRequest("Temporary code missing");
            }

            string? token = await _prusaConnectService.GetToken(clientHeaders.FingerPrint, clientHeaders.TemporaryCode);

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
