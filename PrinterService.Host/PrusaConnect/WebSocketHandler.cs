using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

using Microsoft.Extensions.Logging;

using PrinterService.Data;

namespace PrinterService.Host.PrusaConnect;

public class WebSocketHandler
{
    private readonly PSDbContext _context;
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(PSDbContext context, ILogger<WebSocketHandler> logger)
    {
        _context = context;
        _logger = logger;
    }
    
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowMultipleValues = true,
    };

    public async Task HandlePrusaWebsocket(IWebSocketPipe pipe, CancellationToken cancellationToken)
    {
        while (pipe.State <= WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await pipe.Input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (true)
                {
                    AdvancePastWhitespace(ref buffer);

                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    Utf8JsonReader jsonReader = new(buffer, result.IsCompleted, new JsonReaderState(ReaderOptions));

                    // TryParseValue rather than ParseValue: a document that is merely incomplete is
                    // not malformed, and must not be treated as a protocol violation. Returning
                    // false leaves the bytes in the buffer for a later read to complete.
                    //
                    // No JsonReaderState is carried between iterations. Nothing is consumed until a
                    // document parses in full, so each attempt starts from a clean reader over the
                    // remaining bytes; carrying the state instead causes every document after the
                    // first to be dropped.
                    if (!JsonDocument.TryParseValue(ref jsonReader, out JsonDocument? jsonDocument))
                    {
                        break;
                    }

                    // JsonDocument rents its backing memory from a pool. Failing to return it leaks
                    // on every single telemetry message.
                    using (jsonDocument)
                    {
                        JsonMessageReceived(jsonDocument);
                    }

                    buffer = buffer.Slice(jsonReader.BytesConsumed);
                }
            }
            catch (JsonException e)
            {
                // Bad data from printer, close connection on it and rethrow.
                _logger.LogError(e, "Bad JSON input received from Printer: ");
                await pipe.CompleteAsync(WebSocketCloseStatus.PolicyViolation);
                throw;
            }

            // Consumed up to the end of the last complete document; examined everything. The pipe
            // holds on to the remainder and will not wake us again until more bytes arrive, which
            // is what makes reassembly across reads free.
            pipe.Input.AdvanceTo(buffer.Start, buffer.End);

            // Without this the loop spins on an empty, completed pipe until the socket state
            // happens to change — burning CPU, and hanging outright if it never does.
            if (result.IsCompleted)
            {
                break;
            }
        }

        await pipe.CompleteAsync(WebSocketCloseStatus.NormalClosure);
        _logger.LogInformation("WebSocket handler terminating");
    }

    /// <summary>
    /// Skips whitespace between documents, so a newline separating two messages is never mistaken
    /// for the start of a value.
    /// </summary>
    private static void AdvancePastWhitespace(ref ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> sequenceReader = new(buffer);

        sequenceReader.AdvancePastAny((byte)' ', (byte)'\t', (byte)'\r', (byte)'\n');

        buffer = buffer.Slice(sequenceReader.Position);
    }

    private void JsonMessageReceived(JsonDocument jsonDocument)
    {
        Console.WriteLine(jsonDocument.RootElement.GetRawText());
    }
}
