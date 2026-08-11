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
    /// Frames stay large whatever transport the printer connected over — the WebSocket cap is the only
    /// limit this process answers to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There were two tests here for one day, and this is what replaced them.</b> One asserted
    /// that frames over TLS fit inside 1024 bytes, because Buddy holds that much TLS plaintext
    /// (<c>MBEDTLS_SSL_IN_CONTENT_LEN</c>), asks for it with RFC 6066, and <c>SslStream</c> ignores
    /// the request - so frame size was the only lever this process had over record size. A real MK3.5
    /// failed the transfer on the first chunk without it, proven on hardware 2026-07-31.
    /// </para>
    /// <para>
    /// <b>The lever moved rather than the requirement.</b> nginx terminates printer TLS now, OpenSSL
    /// honours <c>max_fragment_length</c>, and the printer's request is answered at the layer that
    /// negotiated it. So this process is back to writing whatever the WebSocket protocol allows, and
    /// asserting a small frame here would now be asserting a workaround for a problem that is solved
    /// elsewhere - which is exactly the kind of test that outlives its reason and blocks the fix.
    /// </para>
    /// <para>
    /// <b>Nothing in this project can assert the replacement.</b> The guarantee lives in nginx's
    /// OpenSSL, and the E2E pipe carries no TLS at all. It is checked on hardware, and a capture can
    /// confirm the record sizes without decrypting anything - see
    /// <c>WebSocketPrinterConnection.MaxFramePayload</c> for what to do if a transfer fails again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FramesStayLargeWhateverTheTransport()
    {
        // Arrange
        (WebSocketPrinterConnection connection, CaptureStream capture) = NewConnection();

        // Act
        using ArrayContent source = new(new byte[8192]);
        await connection.SendChunkAsync(ChunkWireEncoder.EncodeHeader(1), source, 0, 8192, CancellationToken.None);

        // Assert
        Frame.ParseAll(capture.Written.ToArray()).Should().ContainSingle(
            "8 KB is far inside the frame size, so it is one frame - and there is no longer a "
            + "transport that makes it many");
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
                                                     Justification =
                                                         "The socket and its capture stream live for the duration of the test; neither holds an OS handle.")]
    private static (WebSocketPrinterConnection connection, CaptureStream capture) NewConnection()
    {
        CaptureStream capture = new();
        WebSocket socket = WebSocket.CreateFromStream(capture, isServer: true, null, TimeSpan.FromMinutes(1));

        return (new WebSocketPrinterConnection(socket), capture);
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

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Written.AddRange(buffer.ToArray());
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written.AddRange(buffer.AsSpan(offset, count).ToArray());
        }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0;
        }

        public override void SetLength(long value)
        {
        }
    }
}
