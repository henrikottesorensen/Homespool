using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="LengthLimitingStream"/> - the thing that makes the upload cap real.
/// </summary>
/// <remarks>
/// A <c>Content-Length</c> check is not a limit: the header is optional and a client can lie about
/// it. These assert the cap holds on what is actually read, which is the only version an attacker
/// cannot route around - and an unbounded upload endpoint on an internet-facing deployment is a
/// disk-exhaustion primitive (notes/internet-exposure.md).
/// </remarks>
public class LengthLimitingStreamTests
{
    [Fact]
    public async Task ContentUnderTheLimitReadsThrough()
    {
        // Arrange
        byte[] content = new byte[500];
        await using LengthLimitingStream limited = new(new MemoryStream(content), 1000);

        // Act
        using MemoryStream sink = new();
        await limited.CopyToAsync(sink);

        // Assert
        sink.Length.Should().Be(500);
    }

    [Fact]
    public async Task ContentExactlyAtTheLimitIsAllowed()
    {
        // Arrange
        await using LengthLimitingStream limited = new(new MemoryStream(new byte[1000]), 1000);

        // Act
        using MemoryStream sink = new();
        Func<Task> act = () => limited.CopyToAsync(sink);

        // Assert
        await act.Should().NotThrowAsync("the limit is inclusive - a file of exactly the maximum size is not too large");
    }

    [Fact]
    public async Task ContentOverTheLimitThrows()
    {
        // Arrange
        await using LengthLimitingStream limited = new(new MemoryStream(new byte[1001]), 1000);

        // Act
        using MemoryStream sink = new();
        Func<Task> act = () => limited.CopyToAsync(sink);

        // Assert
        await act.Should().ThrowAsync<UploadTooLargeException>();
    }

    /// <summary>
    /// The case a <c>Content-Length</c> check cannot catch: a body that never declares its size and
    /// simply keeps coming. The limit has to be enforced across reads, not per read.
    /// </summary>
    [Fact]
    public async Task AnEndlessBodyIsStoppedAtTheLimit()
    {
        // Arrange
        await using LengthLimitingStream limited = new(new EndlessStream(), 4096);

        // Act
        using MemoryStream sink = new();
        Func<Task> act = () => limited.CopyToAsync(sink);

        // Assert
        await act.Should().ThrowAsync<UploadTooLargeException>();
        sink.Length.Should().BeLessThan(8192, "it stops near the limit rather than draining the client");
    }

    /// <summary>
    /// The wrapped stream is the request body, which the server owns. Disposing the wrapper must not
    /// close it, or the response could not be written afterwards.
    /// </summary>
    [Fact]
    public async Task DisposingDoesNotCloseTheWrappedStream()
    {
        // Arrange
        MemoryStream inner = new(new byte[10]);

        // Act
        await using (LengthLimitingStream limited = new(inner, 100))
        {
            _ = limited.ReadByte();
        }

        // Assert
        inner.CanRead.Should().BeTrue("the request body outlives this wrapper");
    }

    /// <summary>A body that never ends, to stand in for a client that keeps sending.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => count;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(buffer.Length);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
