using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A browser's WebRTC offer, exchanged for the sidecar's answer through the real endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>The codec check before the exchange is per camera, and the sidecar's refusal is per
/// session</b>: a camera that sends H.264 is offered live view, and a browser whose offer carries
/// none of its codecs is still refused at the exchange. So the sidecar's answer has three outcomes
/// and each reaches the browser as something different.
/// </para>
/// <para>
/// <b>A WebRTC address is configured</b>, which is what makes an H.264 camera watchable over WebRTC
/// at all; without one the endpoint refuses before the sidecar is asked.
/// </para>
/// </remarks>
public sealed class CameraWebRtcOfferTests : IAsyncLifetime
{
    private const string Source = "rtsp://192.0.2.1/h264";
    private const string OfferSdp = "v=0\r\no=browser 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-webrtc");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();

        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
        _factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = "192.0.2.10:8555";

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
    /// The sidecar's answer is handed back as it came, and the browser's offer reached the sidecar
    /// unaltered.
    /// </summary>
    [Fact]
    public async Task AnOfferTheSidecarAnswersIsAnswered()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264);

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "webrtc-answered@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "answered", Source);

            using HttpResponseMessage response = await OfferAsync(client, camera);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            WebRtcDescription? answer = await response.Content.ReadFromJsonAsync<WebRtcDescription>(
                TestContext.Current.CancellationToken);

            answer.Should().Be(new WebRtcDescription("answer", FakeCamera.AnswerSdp));
            _sidecar.Offers.Should().Equal([OfferSdp], "the offer is the browser's and passes through unread");
        }
    }

    /// <summary>
    /// The sidecar refusing the offer's codecs is a conflict - this browser cannot watch this camera
    /// - rather than a fault.
    /// </summary>
    [Fact]
    public async Task AnOfferWithoutTheCamerasCodecsIsAConflict()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264 with { Offer = FakeOfferAnswer.CodecsNotMatched });

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "webrtc-mismatched@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "mismatched", Source);

            using HttpResponseMessage response = await OfferAsync(client, camera);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
    }

    /// <summary>
    /// Any other refusal from the sidecar is a bad gateway: the camera can be watched, and this time
    /// the stream server did not manage it.
    /// </summary>
    [Fact]
    public async Task AnOfferTheSidecarFailsIsABadGateway()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264 with { Offer = FakeOfferAnswer.Fail });

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "webrtc-failed@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "failed", Source);

            using HttpResponseMessage response = await OfferAsync(client, camera);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    private static async Task<HttpResponseMessage> OfferAsync(HttpClient client, Camera camera)
    {
        return await client.PostAsJsonAsync(
            $"/api/v1/cameras/{camera.Uuid}/webrtc",
            new WebRtcDescription("offer", OfferSdp),
            TestContext.Current.CancellationToken);
    }
}
