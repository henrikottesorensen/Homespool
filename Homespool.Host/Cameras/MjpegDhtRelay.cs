using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Cameras;

/// <summary>
/// Relays a multipart MJPEG stream, repairing each frame that omits its Huffman tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>The repair is the reason live MJPEG works in Safari at all.</b> A USB camera's Motion-JPEG
/// frames follow the AVI1 convention, which leaves the Huffman tables (DHT) out of every frame -
/// they are the same standard tables each time, so the convention says why send them. Chrome and
/// Firefox supply the defaults when they are missing; Safari does not decode such a frame at all,
/// and says nothing. Measured on the C910, 2026-08-19: Safari downloaded 13 MB of stream and
/// painted none of it.
/// </para>
/// <para>
/// The stream server already knows about this - its <c>frame.jpeg</c> endpoint injects the tables
/// (<c>mjpeg.FixJPEG</c> → <c>InjectDHT</c>), which is why the polled still always worked
/// everywhere. Its <c>stream.mjpeg</c> endpoint does not, so the repair happens here instead. The
/// injected block below is byte-for-byte the one its still endpoint produces: the four standard
/// tables of RFC 2435 / JPEG Annex K, 432 bytes.
/// </para>
/// <para>
/// <b>Frames that already carry tables pass through untouched</b>, so a network camera that sends
/// complete JPEGs costs nothing but the parse. Anything unexpected - no <c>Content-Length</c>, a
/// part too large to buffer, headers that never end - drops the relay into pass-through, on the
/// principle that a picture Safari cannot decode is still better than a stream nobody gets.
/// </para>
/// </remarks>
public sealed class MjpegDhtRelay
{
    /// <summary>
    /// A part larger than this is not buffered for repair; the relay falls back to pass-through.
    /// Frames from the C910 run ~80 KB; this is two orders of magnitude above that.
    /// </summary>
    private const int MaxPartBytes = 4 * 1024 * 1024;

    /// <summary>
    /// A header block larger than this means the input is not the multipart stream this expects.
    /// </summary>
    private const int MaxHeaderBytes = 4 * 1024;

    private const string ContentLengthName = "Content-Length:";

    /// <summary>
    /// The four standard Huffman tables, as <c>FF C4</c> segments ready to sit before the SOS
    /// marker. Extracted from the stream server's own repaired <c>frame.jpeg</c> output, so the
    /// relay and the still cannot disagree about what a fixed frame looks like.
    /// </summary>
    private static readonly byte[] Dht = Convert.FromBase64String(
        "/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1Fh"
        + "ByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1"
        + "dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx"
        + "8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEE"
        + "BSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpj"
        + "ZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna"
        + "4uPk5ebn6Onq8vP09fb3+Pn6");

    private readonly Stream _upstream;

    private byte[] _buffer = new byte[128 * 1024];
    private int _length;

    public MjpegDhtRelay(Stream upstream)
    {
        _upstream = upstream;
    }

    /// <summary>
    /// Reads until one complete part is buffered, or the stream proves broken. This is the
    /// first-frame liveness check: the stream server writes its 200 before it knows whether it can
    /// serve the camera at all, so a whole part having arrived is the earliest honest moment to
    /// commit an answer downstream.
    /// </summary>
    public async Task<bool> TryBufferFirstPartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Part part = await ReadPartAsync(cancellationToken).ConfigureAwait(false);

            // Pass-through still means bytes arrived, which is what this check is for.
            return part.Kind == PartKind.Complete || (part.Kind == PartKind.PassThrough && _length > 0);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Relays the stream, repairing frames as they pass. Returns when the upstream ends; the caller
    /// owns cancellation, which is how a viewer leaving ends the copy.
    /// </summary>
    public async Task CopyToAsync(Stream downstream, CancellationToken cancellationToken)
    {
        while (true)
        {
            // A part the liveness check buffered is simply parsed again - ReadPartAsync consumes
            // nothing, so the second parse reads the same bytes without touching the stream.
            Part part = await ReadPartAsync(cancellationToken).ConfigureAwait(false);

            if (part.Kind == PartKind.EndOfStream)
            {
                if (_length > 0)
                {
                    await downstream.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken)
                                    .ConfigureAwait(false);
                }

                return;
            }

            if (part.Kind == PartKind.PassThrough)
            {
                await downstream.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken)
                                .ConfigureAwait(false);

                await _upstream.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WritePartAsync(downstream, part, cancellationToken).ConfigureAwait(false);
            Consume(part.TotalLength);

            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Where the repair happens: one part out, tables included.</summary>
    private async Task WritePartAsync(Stream downstream, Part part, CancellationToken cancellationToken)
    {
        int sos = SosIndexIfTablesMissing(part);

        if (sos < 0)
        {
            // Nothing to fix; the original bytes go out as they came in.
            await downstream.WriteAsync(_buffer.AsMemory(0, part.TotalLength), cancellationToken)
                            .ConfigureAwait(false);
            return;
        }

        string headers = Encoding.ASCII.GetString(_buffer, 0, part.HeaderLength);
        string repaired = ReplaceContentLength(headers, part.BodyLength + Dht.Length);
        byte[] headerBytes = Encoding.ASCII.GetBytes(repaired);

        await downstream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        int bodyStart = part.HeaderLength;

        await downstream.WriteAsync(_buffer.AsMemory(bodyStart, sos), cancellationToken)
                        .ConfigureAwait(false);

        await downstream.WriteAsync(Dht, cancellationToken).ConfigureAwait(false);

        await downstream.WriteAsync(
                            _buffer.AsMemory(bodyStart + sos, part.TotalLength - bodyStart - sos),
                            cancellationToken)
                        .ConfigureAwait(false);
    }

    /// <summary>
    /// The offset of the SOS marker within the body when the frame needs tables, or -1 when it
    /// already has them, is not a JPEG, or has no scan to fix.
    /// </summary>
    private int SosIndexIfTablesMissing(Part part)
    {
        ReadOnlySpan<byte> body = _buffer.AsSpan(part.HeaderLength, part.BodyLength);

        if (body.Length < 4 || body[0] != 0xFF || body[1] != 0xD8)
        {
            return -1;
        }

        for (int i = 2; i < body.Length - 1; i++)
        {
            if (body[i] != 0xFF)
            {
                continue;
            }

            byte marker = body[i + 1];

            if (marker == 0xC4)
            {
                return -1; // tables already present
            }

            if (marker == 0xDA)
            {
                return i; // scan starts here and no tables came before it
            }
        }

        return -1;
    }

    private static string ReplaceContentLength(string headers, int newLength)
    {
        int at = headers.IndexOf(ContentLengthName, StringComparison.OrdinalIgnoreCase);
        int valueStart = at + ContentLengthName.Length;
        int valueEnd = headers.IndexOf('\r', valueStart);

        return string.Concat(
            headers.AsSpan(0, valueStart),
            " " + newLength.ToString(CultureInfo.InvariantCulture),
            headers.AsSpan(valueEnd));
    }

    /// <summary>
    /// Parses the next part out of the buffer, reading more as needed. On success the part's bytes
    /// sit at the start of the buffer, described by the result; nothing is consumed here so the
    /// caller can write the original bytes untouched.
    /// </summary>
    private async Task<Part> ReadPartAsync(CancellationToken cancellationToken)
    {
        int headerEnd;

        while ((headerEnd = FindHeaderEnd()) < 0)
        {
            if (_length > MaxHeaderBytes)
            {
                return Part.PassThroughInstead;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return Part.EndOfStreamInstead;
            }
        }

        int headerLength = headerEnd + 4;

        if (ParseContentLength(headerLength) is not { } bodyLength || bodyLength > MaxPartBytes)
        {
            return Part.PassThroughInstead;
        }

        while (_length < headerLength + bodyLength)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return Part.EndOfStreamInstead;
            }
        }

        return new Part(PartKind.Complete, headerLength, bodyLength);
    }

    private int FindHeaderEnd()
    {
        ReadOnlySpan<byte> data = _buffer.AsSpan(0, _length);

        for (int i = 0; i < data.Length - 3; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private int? ParseContentLength(int headerLength)
    {
        string headers = Encoding.ASCII.GetString(_buffer, 0, headerLength);
        int at = headers.IndexOf(ContentLengthName, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return null;
        }

        int valueStart = at + ContentLengthName.Length;
        int valueEnd = headers.IndexOf('\r', valueStart);

        return int.TryParse(headers.AsSpan(valueStart, valueEnd - valueStart), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int length)
            ? length
            : null;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_length == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        int read = await _upstream
                         .ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancellationToken)
                         .ConfigureAwait(false);

        if (read == 0)
        {
            return false;
        }

        _length += read;
        return true;
    }

    private void Consume(int count)
    {
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }

    private enum PartKind
    {
        /// <summary>Never set: Part is a record struct, so default(Part) must not be a real part.</summary>
        Undefined = 0,

        Complete = 1,
        PassThrough = 2,
        EndOfStream = 3,
    }

    private readonly record struct Part(PartKind Kind, int HeaderLength, int BodyLength)
    {
        public static Part PassThroughInstead => new(PartKind.PassThrough, 0, 0);

        public static Part EndOfStreamInstead => new(PartKind.EndOfStream, 0, 0);

        public int TotalLength => HeaderLength + BodyLength;
    }
}
