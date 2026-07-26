using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace PrinterService.Host.PrusaConnect;

public class WebSocketHandler
{
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly MessageDispatcher _dispatcher;

    public WebSocketHandler(ILogger<WebSocketHandler> logger, MessageDispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowMultipleValues = true,
    };

    /// <summary>
    /// Reads printer messages until <paramref name="input"/> completes (the peer closed) or
    /// <paramref name="cancellationToken"/> fires, classifying each one and posting it to
    /// <paramref name="actor"/>. Pure parsing over a <see cref="PipeReader"/>: it never touches the
    /// socket, so closing it - normally on return, with <c>PolicyViolation</c> on the
    /// <see cref="JsonException"/> this throws for malformed input - is the caller's job.
    /// </summary>
    /// <exception cref="JsonException">The printer sent malformed JSON - a protocol violation, not
    /// to be confused with a merely incomplete document, which is buffered instead.</exception>
    /// <remarks>
    /// <c>virtual</c> only so tests can substitute an end for the read loop - throwing, or returning
    /// - without a socket to produce one. Same seam as <c>RecordingMessageDispatcher</c>'s.
    /// </remarks>
    public virtual async Task HandlePrusaWebsocket(PipeReader input, int printerId, IPrinterConnectionActor actor, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await input.ReadAsync(cancellationToken);
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

                    JsonDocument? jsonDocument;
                    long bytesConsumed;

                    // Block scope: Utf8JsonReader is a ref struct and must not be in scope across
                    // the PostAsync await below.
                    {
                        Utf8JsonReader jsonReader = new(buffer, result.IsCompleted, new JsonReaderState(ReaderOptions));

                        // TryParseValue rather than ParseValue: a document that is merely incomplete is
                        // not malformed, and must not be treated as a protocol violation. Returning
                        // false leaves the bytes in the buffer for a later read to complete.
                        //
                        // No JsonReaderState is carried between iterations. Nothing is consumed until a
                        // document parses in full, so each attempt starts from a clean reader over the
                        // remaining bytes; carrying the state instead causes every document after the
                        // first to be dropped.
                        if (!JsonDocument.TryParseValue(ref jsonReader, out jsonDocument))
                        {
                            break;
                        }

                        bytesConsumed = jsonReader.BytesConsumed;
                    }

                    // JsonDocument rents its backing memory from a pool. Failing to return it leaks
                    // on every single telemetry message.
                    using (jsonDocument)
                    {
                        ConnectionMessage? message = _dispatcher.Classify(printerId, jsonDocument.RootElement);

                        if (message is not null)
                        {
                            // PostAsync waits when the mailbox is full, which stops this read loop -
                            // deliberate: a stalled actor becomes TCP backpressure on the printer
                            // instead of unbounded buffering here.
                            await actor.PostAsync(message, cancellationToken);
                        }
                    }

                    buffer = buffer.Slice(bytesConsumed);
                }
            }
            catch (JsonException e)
            {
                // Bad data from printer. Rethrow so the caller closes the connection on it.
                _logger.LogError(e, "Bad JSON input received from Printer: ");
                throw;
            }

            // Consumed up to the end of the last complete document; examined everything. The reader
            // holds on to the remainder and will not wake us again until more bytes arrive, which
            // is what makes reassembly across reads free.
            input.AdvanceTo(buffer.Start, buffer.End);

            // The reader completes when the printer closes (or drops) the connection; the remaining
            // bytes were just drained, so this is the natural end of the stream.
            if (result.IsCompleted)
            {
                break;
            }
        }

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
}
