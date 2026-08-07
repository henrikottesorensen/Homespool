using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Exceptions;

namespace Homespool.Host.PrusaConnect;

public class WebSocketHandler
{
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly MessageDispatcher _dispatcher;
    private readonly PrusaConnectOptions _options;

    public WebSocketHandler(ILogger<WebSocketHandler> logger,
                            MessageDispatcher dispatcher,
                            IOptions<PrusaConnectOptions> options)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _options = options.Value;
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
    /// <exception cref="PrinterMessageTooLargeException">A document stayed incomplete past
    /// <see cref="PrusaConnectOptions.MaxIncomingMessageBytes"/>. Also a protocol violation: an
    /// incomplete document is buffered, but not for ever.</exception>
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
                            try
                            {
                                // PostAsync waits when the mailbox is full, which stops this read loop -
                                // deliberate: a stalled actor becomes TCP backpressure on the printer
                                // instead of unbounded buffering here.
                                await actor.PostAsync(message, cancellationToken);
                            }
                            catch (ChannelClosedException)
                            {
                                // The actor gave up on this connection - today only when a socket write
                                // exceeded its send deadline. That is an ordinary end to the read loop,
                                // not a fault: returning lets the caller close and tear down exactly as
                                // it would for a printer that hung up. Throwing here would instead
                                // surface as an unhandled 500 on a request whose socket is already gone.
                                _logger.LogDebug("actor stopped accepting messages; ending the read loop.");

                                return;
                            }
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

            // Whatever is left is a document that has not finished arriving. Buffering it is the
            // point - the largest real message measured took 184 frames - but only up to a limit,
            // because nothing else bounds this: a value that is opened and never closed would grow
            // the reader's buffer until the process died.
            //
            // Measured here rather than as a running total, because this is exactly the quantity at
            // risk: bytes held while waiting, reset to zero every time a document completes.
            if (buffer.Length > _options.MaxIncomingMessageBytes)
            {
                // Warning with both numbers, because the symptom is a dropped connection and that is
                // indistinguishable from bad wifi without this line. If a real printer trips it, this
                // is what says so and what says which knob to turn.
                _logger.LogWarning(
                    "Printer {PrinterId} buffered {BufferedBytes} bytes without completing a message; "
                    + "the limit is {LimitBytes}. Closing the connection.",
                    printerId,
                    buffer.Length,
                    _options.MaxIncomingMessageBytes);

                throw new PrinterMessageTooLargeException(printerId, buffer.Length, _options.MaxIncomingMessageBytes);
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
