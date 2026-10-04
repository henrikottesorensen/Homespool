using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Http;
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

        // Frames without their Huffman tables, as a USB camera sends them: a Homespool that put the
        // tables back itself would change the bytes, and the byte-for-byte check below would say so.
        _sidecar.AddCamera(Source, FakeCamera.Jpeg with { TablesInStream = false });

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
            response.Content.Headers.ContentType?.ToString().Should().Be("multipart/x-mixed-replace; boundary=frame");
            response.Headers.CacheControl?.NoStore.Should().BeTrue("a frame cached anywhere is a picture of the past");
            response.Headers.GetValues(CustomHeaderNames.AccelBuffering).Should().Equal(["no"],
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
    /// A stream the sidecar drops mid-view breaks the viewer's connection rather than ending the
    /// response: a clean end leaves a browser showing the last frame with nothing to say it stopped,
    /// and a broken one is what the page can see.
    /// </summary>
    [Fact]
    public async Task ACutAtTheSidecarBreaksTheViewersConnection()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-cut@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "cut", Source);

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid);
            _ = await FirstFrameAsync(response);

            await _sidecar.CutMjpegStreamsAsync();

            (await ReadToTheEndAsync(response)).Should().BeFalse(
                "the response must break, not finish - a finished one looks, to a browser, like a picture that stopped changing");
        }
    }

    /// <summary>
    /// A view its page stops is ended by the server: the viewer's connection breaks and the sidecar's
    /// stream closes, whether or not the browser would have let go by itself - Safari does not.
    /// </summary>
    [Fact]
    public async Task AViewItsPageStopsIsEndedByTheServer()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-stopper@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "stopped", Source);
            Guid view = Guid.NewGuid();

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid, view);
            _ = await FirstFrameAsync(response);

            using HttpResponseMessage stopped = await client.DeleteAsync(
                $"/api/v1/cameras/{camera.Uuid}/stream/{view}", TestContext.Current.CancellationToken);

            stopped.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await ReadToTheEndAsync(response)).Should().BeFalse("the server breaks the connection it was asked to end");
            (await EventuallyAsync(() => _sidecar.OpenMjpegStreams == 0)).Should().BeTrue(
                "the camera is released with the view");
        }
    }

    /// <summary>
    /// Another account cannot stop a view, and is told nothing it could not have guessed: its request
    /// is the same 404 as a name never used, and the stream plays on.
    /// </summary>
    [Fact]
    public async Task AnotherAccountCannotStopAView()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-owner@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mjpeg-stranger@example.com");

        using (client)
        using (stranger)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "owned", Source);
            Guid view = Guid.NewGuid();

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid, view);
            _ = await FirstFrameAsync(response);

            using HttpResponseMessage refused = await stranger.DeleteAsync(
                $"/api/v1/cameras/{camera.Uuid}/stream/{view}", TestContext.Current.CancellationToken);
            using HttpResponseMessage unknown = await client.DeleteAsync(
                $"/api/v1/cameras/{camera.Uuid}/stream/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "the stranger's answer must be indistinguishable from this");
            _ = await FirstFrameAsync(response);
            _sidecar.OpenMjpegStreams.Should().Be(1, "the owner's stream plays on");
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
            _sidecar.AddCamera(Source, FakeCamera.Jpeg with { Producing = false, TablesInStream = false });

            using HttpResponseMessage response = await OpenAsync(client, camera.Uuid);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Guid uuid, Guid? view = null)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get, $"/api/v1/cameras/{uuid}/stream.mjpeg" + (view is { } named ? $"?view={named}" : string.Empty));

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the rest of the stream: true if it ended cleanly, false if the connection broke.
    /// </summary>
    private static async Task<bool> ReadToTheEndAsync(HttpResponseMessage response)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);
        byte[] buffer = new byte[4096];

        try
        {
            while (await body.ReadAsync(buffer, deadline.Token) > 0)
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            return false;
        }
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
