using System.Buffers;
using System.Collections.Generic;
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
                            IOptionsMonitor<PrusaConnectOptions> options)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _options = options.CurrentValue;
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
    public virtual async Task HandlePrusaWebsocket(PipeReader input,
                                                   int printerId,
                                                   IPrinterConnectionActor actor,
                                                   CancellationToken cancellationToken)
    {
        // One per connection: it remembers how far into an unfinished document it has already looked,
        // so a document arriving in many reads is scanned once rather than re-parsed on every read.
        JsonDocumentScanner scanner = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (true)
                {
                    // Only ever moves between documents: a document the scanner is part-way through
                    // starts with '{' or a comment, never with whitespace, so its start stays put.
                    AdvancePastWhitespace(ref buffer);

                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    // A document that is merely incomplete is not malformed, and must not be treated
                    // as a protocol violation: returning false leaves the bytes in the buffer for a
                    // later read to complete - unless there will be no later read.
                    if (!scanner.TryFindEnd(buffer, out long bytesConsumed))
                    {
                        // A comment after the last message is not a message left unfinished.
                        if (result.IsCompleted && !scanner.IsBetweenDocuments)
                        {
                            throw new JsonException("The printer closed the connection part-way through a message.");
                        }

                        break;
                    }

                    // The one parse this document gets, over exactly the bytes the scanner found.
                    // Malformed JSON throws here.
                    JsonDocument jsonDocument = ParseDocument(buffer.Slice(0, bytesConsumed),
                                                              out IReadOnlyList<NonFiniteToken> nonFinite);

                    // JsonDocument rents its backing memory from a pool. Failing to return it leaks
                    // on every single telemetry message.
                    using (jsonDocument)
                    {
                        ConnectionMessage? message = _dispatcher.Classify(printerId, jsonDocument.RootElement, nonFinite);

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
                    "Printer {PrinterId} buffered {BufferedBytes} bytes without completing a message; " +
                    "the limit is {LimitBytes}. Closing the connection.",
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
    /// Parses one whole document, mending it first if the only thing wrong with it is a non-finite
    /// number - see <see cref="NonFiniteNumberPatcher"/>.
    /// </summary>
    /// <remarks>
    /// Not inlined because <see cref="Utf8JsonReader"/> is a ref struct, and must not be in scope
    /// across the read loop's awaits.
    /// </remarks>
    private static JsonDocument ParseDocument(ReadOnlySequence<byte> bytes, out IReadOnlyList<NonFiniteToken> nonFinite)
    {
        nonFinite = [];

        try
        {
            return ParseWhole(bytes);
        }
        catch (JsonException)
        {
            // Only now, on a document that is lost anyway: the patcher costs an exception for every
            // token it mends, and an ordinary message never pays for it.
            NonFinitePatch? patch = NonFiniteNumberPatcher.TryPatch(bytes.IsSingleSegment ? bytes.FirstSpan : bytes.ToArray(),
                                                                    ReaderOptions);

            if (patch is null)
            {
                throw;
            }

            nonFinite = patch.Tokens;

            return ParseWhole(new ReadOnlySequence<byte>(patch.Document));
        }
    }

    private static JsonDocument ParseWhole(ReadOnlySequence<byte> bytes)
    {
        Utf8JsonReader jsonReader = new(bytes, isFinalBlock: true, new JsonReaderState(ReaderOptions));

        JsonDocument jsonDocument = JsonDocument.ParseValue(ref jsonReader);

        // The scanner and the reader disagreeing about where the document ends would silently drop
        // whatever the reader left over, so it fails closed instead.
        if (jsonReader.BytesConsumed != bytes.Length)
        {
            jsonDocument.Dispose();

            throw new JsonException($"The message ended at byte {jsonReader.BytesConsumed}, not at byte {bytes.Length}.");
        }

        return jsonDocument;
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
