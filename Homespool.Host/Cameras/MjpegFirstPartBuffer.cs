using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Cameras;

/// <summary>
/// Holds a multipart MJPEG stream back until its first whole part has arrived, then passes the stream
/// on untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first part is the proof that a camera is live.</b> The stream server writes its 200 before
/// it knows whether it can serve the camera at all - a source it cannot carry answers success and
/// then silence - so a whole part having arrived is the earliest honest moment to commit an answer
/// downstream.
/// </para>
/// <para>
/// <b>Nothing is parsed or changed after it.</b> A USB camera's AVI1 frames leave out their Huffman
/// tables, and Safari will not paint a frame without them; the camera sidecar's image puts them back
/// in its own <c>stream.mjpeg</c> (go2rtc/patches), so frames arrive here complete. A go2rtc without
/// that patch, pointed at by <c>Cameras:StreamServerBaseUrl</c>, streams to every browser but
/// Safari.
/// </para>
/// <para>
/// Anything unexpected before the first part - no <c>Content-Length</c>, a part too large to buffer,
/// headers that never end - is passed on as it came, since bytes arriving is what the check is for.
/// </para>
/// </remarks>
public sealed class MjpegFirstPartBuffer
{
    /// <summary>
    /// A first part larger than this is not waited for; what arrived is passed on as it came. Frames
    /// from the C910 run ~80 KB; this is two orders of magnitude above that.
    /// </summary>
    private const int MaxPartBytes = 4 * 1024 * 1024;

    /// <summary>
    /// A header block larger than this means the input is not the multipart stream this expects.
    /// </summary>
    private const int MaxHeaderBytes = 4 * 1024;

    private const string ContentLengthName = "Content-Length:";

    private readonly Stream _upstream;

    private byte[] _buffer = new byte[128 * 1024];
    private int _length;

    public MjpegFirstPartBuffer(Stream upstream)
    {
        _upstream = upstream;
    }

    /// <summary>
    /// Reads until one complete part is buffered, or the stream proves broken.
    /// </summary>
    public async Task<bool> TryBufferFirstPartAsync(CancellationToken cancellationToken)
    {
        try
        {
            PartKind part = await ReadFirstPartAsync(cancellationToken).ConfigureAwait(false);

            // Unframed still means bytes arrived, which is what this check is for.
            return part == PartKind.Complete || (part == PartKind.Unframed && _length > 0);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes what was buffered, then copies the rest of the stream as it comes. Returns when the
    /// upstream ends; the caller owns cancellation, which is how a viewer leaving ends the copy.
    /// </summary>
    public async Task CopyToAsync(Stream downstream, CancellationToken cancellationToken)
    {
        if (_length > 0)
        {
            await downstream.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Let go of what the first part needed: a viewer holds a connection for as long as it watches,
        // and has no use for a frame's worth of memory once that frame is sent.
        _buffer = [];
        _length = 0;

        await _upstream.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads until the buffer holds the first part whole, or the stream ends or turns out not to be
    /// framed the way this expects.
    /// </summary>
    private async Task<PartKind> ReadFirstPartAsync(CancellationToken cancellationToken)
    {
        int headerEnd;

        while ((headerEnd = FindHeaderEnd()) < 0)
        {
            if (_length > MaxHeaderBytes)
            {
                return PartKind.Unframed;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return PartKind.EndOfStream;
            }
        }

        int headerLength = headerEnd + 4;

        if (ParseContentLength(headerLength) is not int bodyLength || bodyLength > MaxPartBytes)
        {
            return PartKind.Unframed;
        }

        while (_length < headerLength + bodyLength)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return PartKind.EndOfStream;
            }
        }

        return PartKind.Complete;
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
                            CultureInfo.InvariantCulture, out int length) ?
            length :
            null;
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

    private enum PartKind
    {
        /// <summary>Reserved so a default value is not a meaningful answer.</summary>
        Undefined = 0,

        /// <summary>The first part is buffered whole.</summary>
        Complete = 1,

        /// <summary>The stream is not framed as expected; what arrived is passed on as it came.</summary>
        Unframed = 2,

        /// <summary>The stream ended before the first part was whole.</summary>
        EndOfStream = 3,
    }
}
