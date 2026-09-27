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
/// The first part as the proof that a camera is live, and the stream passed on untouched after it.
/// </summary>
public class MjpegFirstPartBufferTests
{
    /// <summary>A minimal JPEG in the AVI1 shape a USB camera sends: SOI, APP0, SOF, SOS, data, EOI - and no DHT.</summary>
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

        MjpegFirstPartBuffer firstPart = new(upstream);

        (await firstPart.TryBufferFirstPartAsync(CancellationToken.None)).Should().BeTrue();
        await firstPart.CopyToAsync(downstream, CancellationToken.None);

        return downstream.ToArray();
    }

    /// <summary>
    /// The repair is the camera sidecar's now, so a frame without tables leaves exactly as it came -
    /// the first and every one after it.
    /// </summary>
    [Fact]
    public async Task AStreamIsPassedOnByteForByte()
    {
        byte[] input = [.. Part(FrameWithoutTables()), .. Part(FrameWithoutTables()), .. Part(FrameWithoutTables())];

        byte[] output = await RelayAsync(input);

        output.Should().Equal(input);
    }

    [Fact]
    public async Task InputThatIsNotMultipartFallsThroughUnmodified()
    {
        // No Content-Length header, so the first part cannot be framed: everything must still arrive.
        byte[] input = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\nnot really a jpeg");

        byte[] output = await RelayAsync(input);

        output.Should().Equal(input);
    }

    [Fact]
    public async Task AStreamThatEndsMidPartFailsTheLivenessCheck()
    {
        byte[] whole = Part(FrameWithoutTables());
        byte[] truncated = whole[..(whole.Length - 10)];

        using MemoryStream upstream = new(truncated);

        MjpegFirstPartBuffer firstPart = new(upstream);

        // The first part never completes, so the liveness check honestly says no.
        (await firstPart.TryBufferFirstPartAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ASilentUpstreamFailsTheLivenessCheckInsteadOfHanging()
    {
        // A stream that never produces anything, like the sidecar refusing a codec after its 200.
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        MjpegFirstPartBuffer firstPart = new(client);

        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(200));

        (await firstPart.TryBufferFirstPartAsync(timeout.Token)).Should().BeFalse();
    }
}
