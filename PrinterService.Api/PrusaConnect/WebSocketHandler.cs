using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

using Microsoft.Extensions.Logging;

using PrinterService.Data;

namespace PrinterService.Api.PrusaConnect;

public class WebSocketHandler
{
    private readonly PSDbContext _context;
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(PSDbContext context, ILogger<WebSocketHandler> logger)
    {
        _context = context;
        _logger = logger;
    }
    
    public async Task HandlePrusaWebsocket(IWebSocketPipe pipe, CancellationToken cancellationToken)
    {
        JsonReaderState jsonState = new(new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            AllowMultipleValues = true,
        });
        
        while (pipe.State <= WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            // Can we received a fragmented json object here, and race parsing vs. receiving remainder of the object code.. 
            ReadResult result = await pipe.Input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;
            
            try
            {
                while (!buffer.IsEmpty)
                {
                    Utf8JsonReader jsonReader = new(buffer, isFinalBlock: result.IsCompleted, jsonState);

                    while (jsonReader.Read())
                    {
                        JsonDocument jsonDocument = JsonDocument.ParseValue(ref jsonReader);

                        JsonMessageReceived(jsonDocument);
                    }
                    
                    buffer = buffer.Slice(jsonReader.BytesConsumed);
                    jsonState = jsonReader.CurrentState;
                }

                pipe.Input.AdvanceTo(buffer.Start, buffer.End);
            }
            catch (JsonException e)
            {
                // Bad data from printer, close connection on it and rethrow.
                _logger.LogError(e, "Bad JSON input received from Printer: ");
                await pipe.CompleteAsync(WebSocketCloseStatus.PolicyViolation);
                throw;
            }
        }
        
        await pipe.CompleteAsync(WebSocketCloseStatus.NormalClosure);
        _logger.LogInformation("WebSocket handler terminating");
    }

    private void JsonMessageReceived(JsonDocument jsonDocument)
    {
        Console.WriteLine(jsonDocument.RootElement.GetRawText());
    }
}
