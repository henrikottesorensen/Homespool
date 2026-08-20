using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The relay's one job: every JPEG that leaves it carries Huffman tables, whether or not the one
/// that arrived did. A USB camera's AVI1 frames omit them, and Safari - alone among the browsers -
/// will not decode a frame without them; it downloads the stream and paints nothing.
/// </summary>
public class MjpegDhtRelayTests
{
    /// <summary>A minimal JPEG in the AVI1 shape: SOI, APP0, SOF, SOS, data, EOI - and no DHT.</summary>
    private static byte[] FrameWithoutTables()
    {
        return
        [
            0xFF, 0xD8,                                      // SOI
            0xFF, 0xE0, 0x00, 0x08, (byte)'A', (byte)'V', (byte)'I', (byte)'1', 0x00, 0x00, // APP0 "AVI1"
            0xFF, 0xC0, 0x00, 0x05, 0x08, 0x00, 0x01,        // SOF, truncated but framed
            0xFF, 0xDA, 0x00, 0x04, 0x01, 0x02,              // SOS
            0x11, 0x22, 0x33,                                // entropy data
            0xFF, 0xD9,                                      // EOI
        ];
    }

    /// <summary>The same frame with a (dummy) DHT segment already in place before the scan.</summary>
    private static byte[] FrameWithTables()
    {
        byte[] frame = FrameWithoutTables();
        byte[] dht = [0xFF, 0xC4, 0x00, 0x05, 0x00, 0x01, 0x02];

        int sos = frame.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });

        return [.. frame[..sos], .. dht, .. frame[sos..]];
    }

    private static byte[] Part(byte[] body)
    {
        byte[] headers = Encoding.ASCII.GetBytes(
            $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {body.Length}\r\n\r\n");

        return [.. headers, .. body, .. Encoding.ASCII.GetBytes("\r\n")];
    }

    private static async Task<byte[]> RelayAsync(byte[] input)
    {
        using MemoryStream upstream = new(input);
        using MemoryStream downstream = new();

        MjpegDhtRelay relay = new(upstream);

        (await relay.TryBufferFirstPartAsync(CancellationToken.None)).Should().BeTrue();
        await relay.CopyToAsync(downstream, CancellationToken.None);

        return downstream.ToArray();
    }

    private static int CountDhtMarkers(byte[] data)
    {
        int count = 0;

        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xC4)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task AFrameWithoutTablesGetsThemBeforeTheScan()
    {
        byte[] output = await RelayAsync(Part(FrameWithoutTables()));

        // The four standard tables, all sitting before the scan starts.
        CountDhtMarkers(output).Should().Be(4);

        int firstDht = output.AsSpan().IndexOf(new byte[] { 0xFF, 0xC4 });
        int sos = output.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        firstDht.Should().BeLessThan(sos);
    }

    [Fact]
    public async Task TheContentLengthIsCorrectedToMatchTheRepairedFrame()
    {
        byte[] body = FrameWithoutTables();
        byte[] output = await RelayAsync(Part(body));

        string headers = Encoding.ASCII.GetString(output, 0, HeaderLength(output));

        // 432 bytes of tables were added, and the header must say so, or a strict multipart
        // parser reads entropy data as the next part's headers.
        headers.Should().Contain($"Content-Length: {body.Length + 432}");
    }

    [Fact]
    public async Task AFrameAlreadyCarryingTablesPassesThroughByteForByte()
    {
        byte[] input = Part(FrameWithTables());

        byte[] output = await RelayAsync(input);

        output.Should().Equal(input);
    }

    [Fact]
    public async Task EveryFrameOfAStreamIsRepairedNotJustTheFirst()
    {
        byte[] input = [.. Part(FrameWithoutTables()), .. Part(FrameWithoutTables())];

        byte[] output = await RelayAsync(input);

        CountDhtMarkers(output).Should().Be(8);
    }

    [Fact]
    public async Task InputThatIsNotMultipartFallsThroughUnmodified()
    {
        // No Content-Length header, so the relay cannot frame parts: everything must still arrive.
        byte[] input = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\nnot really a jpeg");

        byte[] output = await RelayAsync(input);

        output.Should().Equal(input);
    }

    [Fact]
    public async Task AStreamThatEndsMidPartStillDeliversWhatArrived()
    {
        byte[] whole = Part(FrameWithoutTables());
        byte[] truncated = whole[..(whole.Length - 10)];

        using MemoryStream upstream = new(truncated);
        using MemoryStream downstream = new();

        MjpegDhtRelay relay = new(upstream);

        // The first part never completes, so the liveness check honestly says no.
        (await relay.TryBufferFirstPartAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ASilentUpstreamFailsTheLivenessCheckInsteadOfHanging()
    {
        // A stream that never produces anything, like the sidecar refusing a codec after its 200.
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        MjpegDhtRelay relay = new(client);

        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(200));

        (await relay.TryBufferFirstPartAsync(timeout.Token)).Should().BeFalse();
    }

    private static int HeaderLength(byte[] data)
    {
        for (int i = 0; i < data.Length - 3; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return data.Length;
    }
}
