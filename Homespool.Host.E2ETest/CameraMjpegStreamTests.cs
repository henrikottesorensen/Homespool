using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A live MJPEG view through the real endpoint, relayed from a sidecar that streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frames arrive as the sidecar sent them.</b> Putting back the Huffman tables a USB camera leaves
/// out is the sidecar image's job, so Homespool must not change a byte of the frames it relays.
/// </para>
/// <para>
/// <b>A viewer leaving has to reach the sidecar</b>: the stream holds the camera open for as long as
/// its connection lasts, so a relay that kept reading after the browser went would hold it for
/// nobody.
/// </para>
/// </remarks>
public sealed class CameraMjpegStreamTests : IAsyncLifetime
{
    private const string Source = "rtsp://192.0.2.1/mjpeg";

    private static readonly byte[] StartOfImage = [0xFF, 0xD8];
    private static readonly byte[] EndOfImage = [0xFF, 0xD9];

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-mjpeg");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();
        _sidecar.AddCamera(Source, FakeCamera.Jpeg);

        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The stream is answered as a stream nothing may hold back, and its frames arrive exactly as the
    /// sidecar sent them.
    /// </summary>
    [Fact]
    public async Task AStreamIsRelayedAsTheSidecarSendsIt()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-viewer@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "mjpeg", Source);

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("multipart/x-mixed-replace");
            response.Headers.CacheControl?.NoStore.Should().BeTrue("a frame cached anywhere is a picture of the past");
            response.Headers.GetValues("X-Accel-Buffering").Should().Equal(["no"],
                                                                         "the front proxy would otherwise hold every frame back");

            byte[] frame = await FirstFrameAsync(response);

            frame.Should().Equal(FakeCamera.Frame.ToArray(), "relaying a frame is not a licence to change it");
        }
    }

    /// <summary>
    /// A viewer closing the stream closes the sidecar's too, releasing the camera.
    /// </summary>
    [Fact]
    public async Task AViewerLeavingReleasesTheCamera()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-leaver@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "leaving", Source);

            using (HttpResponseMessage response = await OpenAsync(client, camera.Uuid))
            {
                _ = await FirstFrameAsync(response);
                _sidecar.OpenMjpegStreams.Should().Be(1, "the stream must be open, or its closing proves nothing");
            }

            (await EventuallyAsync(() => _sidecar.OpenMjpegStreams == 0)).Should().BeTrue(
                "the sidecar's stream must end with the viewer's");
        }
    }

    /// <summary>
    /// A sidecar that answers 200 and then sends no picture is not a stream: the viewer is told the
    /// camera produced nothing, rather than handed an empty one.
    /// </summary>
    [Fact]
    public async Task ASidecarThatSendsNoPictureIsABadGateway()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-dark@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "dark", Source);

            // Switched off after it was saved, so its codecs are already known and the endpoint gets
            // as far as asking the sidecar for pictures.
            _sidecar.AddCamera(Source, FakeCamera.Jpeg with { Producing = false });

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Guid uuid)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/cameras/{uuid}/stream.mjpeg");

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }

    /// <summary>The first JPEG in the stream, from its start marker to its end marker.</summary>
    private static async Task<byte[]> FirstFrameAsync(HttpResponseMessage response)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);
        using MemoryStream seen = new();
        byte[] buffer = new byte[4096];

        while (true)
        {
            int read = await body.ReadAsync(buffer, deadline.Token);
            read.Should().BePositive("the stream ended before a whole frame arrived");

            await seen.WriteAsync(buffer.AsMemory(0, read), deadline.Token);

            byte[] bytes = seen.ToArray();
            int start = bytes.AsSpan().IndexOf(StartOfImage);
            int end = start < 0 ? -1 : bytes.AsSpan(start).IndexOf(EndOfImage);

            if (end >= 0)
            {
                return bytes[start..(start + end + EndOfImage.Length)];
            }
        }
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return condition();
    }
}
