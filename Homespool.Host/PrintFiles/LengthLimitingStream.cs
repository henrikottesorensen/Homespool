using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Wraps a request body and throws once more than <c>limit</c> bytes have been read from it.
/// </summary>
/// <remarks>
/// A <c>Content-Length</c> check alone is not a limit: the header is optional, and a client that
/// omits it or lies about it would otherwise stream until the disk filled. This bounds what is
/// actually read, so the cap holds regardless of what the client claimed - which matters on an
/// internet-facing deployment, where an unbounded upload endpoint is a disk-exhaustion primitive
/// (notes/internet-exposure.md).
/// </remarks>
[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
                 Justification =
                     "The wrapped stream is the request body, owned by the server. Disposing it here would close the request mid-pipeline; see Dispose.")]
public sealed class LengthLimitingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public LengthLimitingStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = await _inner.ReadAsync(buffer, cancellationToken);

        Count(count);

        return count;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        Count(read);

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);

        Count(read);

        return read;
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    /// <summary>
    /// Deliberately does not dispose the wrapped stream. This wraps the request body, which the
    /// server owns and disposes as part of the request lifetime - closing it here would tear the
    /// request down while MVC is still writing the response.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private void Count(int read)
    {
        _read += read;

        if (_read > _limit)
        {
            throw new UploadTooLargeException(_limit);
        }
    }
}

/// <summary>Thrown when an upload exceeds <see cref="PrintFileStorageOptions.MaxUploadBytes"/>.</summary>
public sealed class UploadTooLargeException : Exception
{
    public UploadTooLargeException(long limit)
        : base($"Upload exceeded the {limit}-byte limit.")
    {
        Limit = limit;
    }

    public UploadTooLargeException()
    {
    }

    public UploadTooLargeException(string message)
        : base(message)
    {
    }

    public UploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public long Limit { get; }
}
