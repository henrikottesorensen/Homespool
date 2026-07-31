using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Test;

/// <summary>
/// The wire framing of a transfer chunk, asserted on the actual bytes a real
/// <see cref="System.Net.WebSockets.WebSocket"/> puts on a stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is worth testing at the byte level.</b> A WebSocket frame encodes its length in 7, 16
/// or 64 bits, and firmware's client supports only the first two - it rejects the 64-bit form as
/// <c>Error::WebSocket</c> and drops the connection (websocket.cpp:127-129). .NET emits exactly one
/// frame per <c>SendAsync</c> call and never fragments on its own, so a 256 KiB chunk written in one
/// call goes out with the 64-bit marker and kills the transfer. Nothing above the socket can see that
/// - it is not an exception, not a status, just a connection that dies - so it is asserted here,
/// where the bytes are visible.
/// </para>
/// <para>
/// A real <c>WebSocket</c> over a capturing stream rather than a mock, because the thing under test
/// <i>is</i> .NET's framing behaviour. Server-side (<c>isServer: true</c>), which also matches
/// production: firmware rejects masked frames from a server outright (websocket.cpp:112-116).
/// </para>
/// </remarks>
public class TransferChunkFramingTests
{
    /// <summary>The largest payload the 16-bit length encoding can express, and so the largest frame
    /// firmware will accept.</summary>
    private const int MaxAcceptableFramePayload = 65535;

    /// <summary>
    /// The case that motivates all of this: a full-size segment, which is over four frames' worth.
    /// </summary>
    [Fact]
    public async Task AFullSegmentIsSplitIntoFramesTheFirmwareCanDecode()
    {
        // Arrange
        const int segment = 256 * 1024;
        byte[] content = Enumerable.Range(0, segment).Select(i => (byte)(i % 251)).ToArray();
        byte[] header = ChunkWireEncoder.EncodeHeader(0xDEADBEEF);

        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection();

        // Act
        using ArrayContent source = new(content);
        await connection.SendChunkAsync(header, source, 0, segment, CancellationToken.None);

        // Assert
        IReadOnlyList<Frame> frames = Frame.ParseAll(capture.Written.ToArray());

        frames.Should().HaveCountGreaterThan(1, "256 KiB cannot fit in one acceptable frame");
        frames.Should().OnlyContain(f => f.Payload.Length <= MaxAcceptableFramePayload,
            "a frame over 65535 bytes is written with the 64-bit length marker, which firmware rejects");

        // Exactly one message: continuation frames until the last, which carries FIN.
        frames.Take(frames.Count - 1).Should().OnlyContain(f => !f.Fin);
        frames[^1].Fin.Should().BeTrue();

        // The header appears once, at the front - firmware parses it once per message and treats the
        // rest as payload (connect.cpp:445-532).
        byte[] whole = frames.SelectMany(f => f.Payload).ToArray();
        Encoding.ASCII.GetString(whole, 0, 9).Should().Be("TDEADBEEF");
        whole.AsSpan(9).ToArray().Should().Equal(content, "every byte of the requested range, in order");
    }

    /// <summary>
    /// A chunk small enough to fit stays a single frame - the fragmenting must not impose a shape on
    /// the ordinary case.
    /// </summary>
    [Fact]
    public async Task ASmallChunkIsASingleFrame()
    {
        // Arrange
        byte[] content = new byte[1000];
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection();

        // Act
        using ArrayContent source = new(content);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, content.Length,
            CancellationToken.None);

        // Assert
        IReadOnlyList<Frame> frames = Frame.ParseAll(capture.Written.ToArray());
        frames.Should().ContainSingle();
        frames[0].Fin.Should().BeTrue();
        frames[0].Payload.Should().HaveCount(9 + content.Length);
    }

    /// <summary>
    /// The boundary the whole thing turns on. A chunk whose message is exactly 65535 bytes must stay
    /// one frame; one byte more must become two - because at 65536 .NET switches to the length
    /// encoding firmware refuses.
    /// </summary>
    [Theory]
    [InlineData(MaxAcceptableFramePayload - 9, 1)]
    [InlineData(MaxAcceptableFramePayload - 8, 2)]
    public async Task TheFrameBoundaryIsExactlyTheSixteenBitCeiling(int count, int expectedFrames)
    {
        // Arrange
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection();

        // Act
        using ArrayContent source = new(new byte[count]);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, count, CancellationToken.None);

        // Assert
        IReadOnlyList<Frame> frames = Frame.ParseAll(capture.Written.ToArray());
        frames.Should().HaveCount(expectedFrames);
        frames.Should().OnlyContain(f => f.Payload.Length <= MaxAcceptableFramePayload);
    }

    /// <summary>
    /// Over TLS every frame has to fit the printer's TLS record buffer, which is a different and
    /// much smaller limit than the WebSocket one.
    /// </summary>
    /// <remarks>
    /// Buddy holds 1024 bytes of TLS plaintext (<c>MBEDTLS_SSL_IN_CONTENT_LEN</c>) and asks for that
    /// with RFC 6066; <c>SslStream</c> ignores the request and sizes records by how much it is given
    /// per write, so the frame size is the only lever. A real MK3.5 fails the transfer on the first
    /// chunk without this - proven on hardware 2026-07-31 - and no in-process test can see it,
    /// because the E2E pipe carries no TLS. This asserts the lever, which is the part that can rot.
    /// </remarks>
    [Fact]
    public async Task OverTlsFramesFitThePrintersRecordBuffer()
    {
        // Arrange
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection(overTls: true);

        // Act
        using ArrayContent source = new(new byte[8192]);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, 8192, CancellationToken.None);

        // Assert
        IReadOnlyList<Frame> frames = Frame.ParseAll(capture.Written.ToArray());

        frames.Should().OnlyContain(f => f.Payload.Length <= 1024 - 4,
            "the frame plus its header has to fit one 1024-byte TLS record");
        frames.Should().HaveCountGreaterThan(8, "8 KB in ~1 KB frames cannot be fewer");
        frames[^1].Fin.Should().BeTrue("the message still has to end");
    }

    /// <summary>
    /// The same payload in the clear stays in big frames - the small ones are a TLS tax, not a new
    /// default, and paying it on a plaintext connection would be 65x the frames for nothing.
    /// </summary>
    [Fact]
    public async Task InTheClearTheFramesStayLarge()
    {
        // Arrange
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection(overTls: false);

        // Act
        using ArrayContent source = new(new byte[8192]);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, 8192, CancellationToken.None);

        // Assert
        Frame.ParseAll(capture.Written.ToArray()).Should().ContainSingle(
            "8 KB is far inside the plaintext frame size, so it is one frame");
    }

    /// <summary>
    /// Reads that come back short - which an ordinary file read is always allowed to do - must not
    /// end the chunk early. Under-delivering is the one failure firmware cannot recover from: the
    /// inline engine has no stall timeout, so the printer would wait forever.
    /// </summary>
    [Fact]
    public async Task AShortReadIsRetriedRatherThanTruncatingTheChunk()
    {
        // Arrange
        byte[] content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection();

        // Act
        // Never yields more than 64 bytes at a time, so a chunk that ignored the return value would
        // send 64 bytes and stop.
        using ArrayContent source = new(content, maxRead: 64);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, content.Length,
            CancellationToken.None);

        // Assert
        byte[] whole = Frame.ParseAll(capture.Written.ToArray()).SelectMany(f => f.Payload).ToArray();
        whole.AsSpan(9).ToArray().Should().Equal(content);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "The socket and its capture stream live for the duration of the test; neither holds an OS handle.")]

    // overTls defaults to the plaintext frame size, which is what every case here was written
    // against; the encrypted size has its own test rather than being folded into these.
    private static (WebSocketPrinterConnection connection, CaptureStream capture) NewConnection(bool overTls = false)
    {
        CaptureStream capture = new();
        WebSocket socket = WebSocket.CreateFromStream(capture, isServer: true, null, TimeSpan.FromMinutes(1));

        return (new WebSocketPrinterConnection(socket, overTls), capture);
    }

    /// <summary>One parsed frame: enough of RFC 6455 to assert on what was written.</summary>
    private sealed record Frame(bool Fin, byte[] Payload)
    {
        public static IReadOnlyList<Frame> ParseAll(byte[] bytes)
        {
            List<Frame> frames = [];
            int i = 0;

            while (i < bytes.Length)
            {
                bool fin = (bytes[i] & 0x80) != 0;
                int length = bytes[i + 1] & 0x7F;
                int headerLength = 2;

                if (length == 126)
                {
                    length = (bytes[i + 2] << 8) | bytes[i + 3];
                    headerLength = 4;
                }
                else if (length == 127)
                {
                    // Present only if the code under test regressed; parsed so the assertion reports
                    // the oversized frame rather than throwing here.
                    length = 0;

                    for (int k = 0; k < 8; k++)
                    {
                        length = (length << 8) | bytes[i + 2 + k];
                    }

                    headerLength = 10;
                }

                frames.Add(new Frame(fin, bytes.AsSpan(i + headerLength, length).ToArray()));
                i += headerLength + length;
            }

            return frames;
        }
    }

    /// <summary>
    /// Content backed by an array, optionally refusing to fill more than <c>maxRead</c> bytes per
    /// call - which is what an ordinary file read may do, and what the chunk loop has to cope with.
    /// </summary>
    private sealed class ArrayContent(byte[] bytes, int maxRead = int.MaxValue) : ITransferContent
    {
        public long Length => bytes.Length;

        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken)
        {
            int count = Math.Min(Math.Min(destination.Length, maxRead), (int)(bytes.Length - offset));
            bytes.AsSpan((int)offset, count).CopyTo(destination.Span);

            return ValueTask.FromResult(count);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Keeps whatever the WebSocket writes, so the frames can be read back.</summary>
    private sealed class CaptureStream : Stream
    {
        public List<byte> Written { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Written.AddRange(buffer.ToArray());

        public override void Write(byte[] buffer, int offset, int count) =>
            Written.AddRange(buffer.AsSpan(offset, count).ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.AddRange(buffer.ToArray());

            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Written.AddRange(buffer.AsSpan(offset, count).ToArray());

            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }
    }
}
