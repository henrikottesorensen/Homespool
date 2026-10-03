using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Cameras;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// What Homespool relies on the camera sidecar to do, asserted once and run against two of them: the
/// real image and <c>FakeGo2Rtc</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of assertions, so the two cannot quietly disagree.</b> The fake is what lets every E2E
/// camera test run without a sidecar, and each of its answers is a belief about go2rtc. When the real
/// subclass goes red, go2rtc - or our build of it - changed; when the fake one does, the fake has
/// drifted from what it claims to imitate.
/// </para>
/// <para>
/// <b>Homespool's own client and fetcher ask wherever they can</b>, resolved from the same
/// registration the application uses, so what is pinned is the outcome Homespool reads rather than a
/// status it never looks at. Raw requests remain only for the protocol facts a client would hide: the
/// credential, the allowlist, and the form of a delete that does nothing.
/// </para>
/// <para>
/// <b>The cameras are go2rtc's own test pattern</b>, encoded by the ffmpeg in the image - one as
/// H.264, one as Motion-JPEG - and an address in TEST-NET for a camera that is not there. Homespool's
/// source policy would refuse the pattern on the Cameras page; nothing here goes through it.
/// </para>
/// </remarks>
public abstract class Go2RtcContract : IAsyncLifetime
{
    protected const string H264Source = "ffmpeg:virtual?video&size=320x240#video=h264";
    protected const string JpegSource = "ffmpeg:virtual?video&size=320x240#video=mjpeg";
    protected const string AbsentSource = "rtsp://192.0.2.1/live";

    /// <summary>
    /// A browser's offer to receive H.264 video, reduced to what a WebRTC stack needs to answer it.
    /// </summary>
    private const string BrowserOffer =
        "v=0\r\no=- 4611731400430051336 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\n" +
        "a=msid-semantic: WMS\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=rtcp:9 IN IP4 0.0.0.0\r\n" +
        "a=ice-ufrag:abcd\r\na=ice-pwd:abcdefghijklmnopqrstuvwx\r\na=ice-options:trickle\r\n" +
        "a=fingerprint:sha-256 AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB\r\n" + // betterleaks:allow - a made-up certificate fingerprint, not a device address
        "a=setup:actpass\r\na=mid:0\r\na=recvonly\r\na=rtcp-mux\r\n" +
        "a=rtpmap:96 H264/90000\r\na=fmtp:96 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n";

    private ServiceProvider _services = null!;

    /// <summary>Where the sidecar's HTTP API answers.</summary>
    protected abstract Uri BaseAddress { get; }

    /// <summary>The sidecar's RTSP port, on the same host.</summary>
    protected abstract int RtspPort { get; }

    protected abstract string Username { get; }

    protected abstract string Password { get; }

    private Go2RtcClient Client => _services.GetRequiredService<Go2RtcClient>();

    public virtual ValueTask InitializeAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
                                       .AddInMemoryCollection(new Dictionary<string, string?>
                                       {
                                           ["Cameras:ApiUsername"] = Username,
                                           ["Cameras:ApiPassword"] = Password,
                                           ["Cameras:StreamServerBaseUrl"] = BaseAddress.ToString().TrimEnd('/'),
                                           ["Cameras:StreamServerRtspPort"] = RtspPort.ToString(CultureInfo.InvariantCulture),
                                       })
                                       .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCameras(configuration);

        _services = services.BuildServiceProvider();

        return ValueTask.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    /// <summary>Skips the test when there is no sidecar of this kind to ask.</summary>
    protected virtual void RequireSidecar()
    {
    }

    /// <summary>
    /// A request without the credential is refused - the sidecar's own wall, which is what stops a
    /// camera source from sending it back to its own API.
    /// </summary>
    [Fact]
    public async Task ARequestWithoutTheCredentialIsRefused()
    {
        RequireSidecar();

        using HttpClient anonymous = new();
        using HttpResponseMessage response = await anonymous.GetAsync(
            new Uri(BaseAddress, "/api/streams"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A path off the allowlist does not exist: a bare 404 from the mux, not go2rtc's own words.
    /// </summary>
    [Fact]
    public async Task APathOffTheAllowlistIsABareNotFound()
    {
        RequireSidecar();

        using HttpResponseMessage response = await RawAsync(HttpMethod.Get, "/api/exit");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(response)).Should().Be("404 page not found");
    }

    /// <summary>A stream Homespool registers is one the sidecar then lists.</summary>
    [Fact]
    public async Task ARegisteredStreamIsListed()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();

        (await Client.PutStreamAsync(stream, H264Source, TestContext.Current.CancellationToken)).Should().Be(StreamRegistration.Registered);

        (await ListedAsync()).Should().Contain(Name(stream));
    }

    /// <summary>
    /// A delete naming the stream by <c>name</c> answers success and removes nothing - the trap that
    /// once left every deleted camera registered.
    /// </summary>
    [Fact]
    public async Task ADeleteByNameAnswersSuccessAndRemovesNothing()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, H264Source, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await RawAsync(HttpMethod.Delete, $"/api/streams?name={Name(stream)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedAsync()).Should().Contain(Name(stream));
    }

    /// <summary>
    /// Homespool's delete removes the stream and says so - and says so again for a stream already
    /// gone, which is what makes a delete safe to repeat.
    /// </summary>
    [Fact]
    public async Task HomespoolsDeleteRemovesTheStream()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, H264Source, TestContext.Current.CancellationToken);

        (await Client.DeleteStreamAsync(stream, TestContext.Current.CancellationToken)).Should().BeTrue();

        (await ListedAsync()).Should().NotContain(Name(stream));
        (await Client.DeleteStreamAsync(stream, TestContext.Current.CancellationToken)).Should().BeTrue(
            "a name the sidecar does not hold is answered 200, not an error");
    }

    /// <summary>
    /// A registered source reads back from the configuration file exactly as it was given, whichever
    /// way the sidecar quotes it there - which is how a stream already holding a camera's source is
    /// told apart from one that needs replacing. go2rtc writes these plain, and the one ending in a
    /// colon single-quoted.
    /// </summary>
    [Theory]
    [InlineData("rtsp://192.0.2.10/live")]
    [InlineData("ffmpeg:device?video=/dev/v4l/by-id/usb-046d_HD_Pro_Webcam_C920_ABCDEF12-video-index0&input_format=mjpeg&video_size=1920x1080")]
    [InlineData("rtsp://admin:p#ss'w\"o:rd@192.0.2.11:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1")]
    [InlineData("rtsp://192.0.2.20/a:b:")]
    [InlineData("rtsp://192.0.2.21/?a=[1,2]&b={c:d}&e=*f&g=!h&i=%25j")]
    [InlineData("rtsp://192.0.2.14/kamera-æøå")]
    [InlineData("http://192.0.2.15/x#")]
    public async Task ARegisteredSourceReadsBackFromTheConfigurationAsGiven(string source)
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        (await Client.PutStreamAsync(stream, source, TestContext.Current.CancellationToken)).Should().Be(StreamRegistration.Registered);

        IReadOnlyDictionary<string, IReadOnlyList<string>>? saved =
            await Client.ReadStreamSourcesAsync(TestContext.Current.CancellationToken);

        saved.Should().NotBeNull();
        saved![Name(stream)].Should().Equal(source);

        (await Client.DeleteStreamAsync(stream, TestContext.Current.CancellationToken)).Should().BeTrue();

        (await Client.ReadStreamSourcesAsync(TestContext.Current.CancellationToken))!.Should().NotContainKey(Name(stream));
    }

    /// <summary>
    /// A stream being watched still reads back from the file as the source it was given. The listing
    /// reports a watched stream by its running connection instead, which for an <c>ffmpeg:</c> source
    /// is a command line - so the file is the only place the question can be asked of.
    /// </summary>
    [Fact]
    public async Task AWatchedStreamStillReadsBackAsItsSource()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, JpegSource, TestContext.Current.CancellationToken);

        using LiveMjpegStream? live = await OpenStreamAsync(stream);
        live.Should().NotBeNull("the stream must be running, or this asks nothing a cold stream does not");

        IReadOnlyDictionary<string, IReadOnlyList<string>>? saved =
            await Client.ReadStreamSourcesAsync(TestContext.Current.CancellationToken);

        saved![Name(stream)].Should().Equal(JpegSource);
    }

    /// <summary>A stream the sidecar does not hold has no frame, in go2rtc's own words.</summary>
    [Fact]
    public async Task AnUnknownStreamHasNoFrame()
    {
        RequireSidecar();

        using HttpResponseMessage response = await RawAsync(HttpMethod.Get, $"/api/frame.jpeg?src={Name(Guid.NewGuid())}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(response)).Should().Be("stream not found");
    }

    /// <summary>A camera producing pictures gives Homespool's fetcher a JPEG.</summary>
    [Fact]
    public async Task AProducingCameraGivesAFrame()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, JpegSource, TestContext.Current.CancellationToken);

        CameraFrame? frame = await FetchFrameAsync(stream);

        frame.Should().NotBeNull();
        frame!.ContentType.Should().Be("image/jpeg");
        frame.Bytes.Should().NotBeEmpty();
    }

    /// <summary>
    /// A camera that is not there gives Homespool's fetcher nothing it takes for a picture - whatever
    /// status the sidecar wraps that in.
    /// </summary>
    [Fact]
    public async Task AnAbsentCameraGivesNoFrame()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, AbsentSource, TestContext.Current.CancellationToken);

        (await FetchFrameAsync(stream)).Should().BeNull();
    }

    /// <summary>
    /// The codec probe - an RTSP <c>DESCRIBE</c> - names what each camera is encoded as, which is
    /// what decides whether it is watched over WebRTC or MJPEG.
    /// </summary>
    [Fact]
    public async Task TheProbeNamesEachCamerasCodec()
    {
        RequireSidecar();
        Guid h264 = Guid.NewGuid();
        Guid jpeg = Guid.NewGuid();
        await Client.PutStreamAsync(h264, H264Source, TestContext.Current.CancellationToken);
        await Client.PutStreamAsync(jpeg, JpegSource, TestContext.Current.CancellationToken);

        (await Client.ProbeCodecsAsync(h264, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(["H264"]);
        (await Client.ProbeCodecsAsync(jpeg, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(["JPEG"]);
    }

    /// <summary>
    /// The probe has no answer for a camera that is not there - which the caller must not remember
    /// as "no codecs".
    /// </summary>
    [Fact]
    public async Task TheProbeHasNoAnswerForAnAbsentCamera()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, AbsentSource, TestContext.Current.CancellationToken);

        (await Client.ProbeCodecsAsync(stream, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>A browser's offer for an H.264 camera is answered.</summary>
    [Fact]
    public async Task AnOfferForAnH264CameraIsAnswered()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, H264Source, TestContext.Current.CancellationToken);

        WebRtcOffer answer = await Client.OfferAsync(stream, BrowserOffer, TestContext.Current.CancellationToken);

        answer.Outcome.Should().Be(WebRtcOfferOutcome.Answered);
        answer.Sdp.Should().StartWith("v=0");
    }

    /// <summary>
    /// An offer the camera's codec cannot meet is read as exactly that - a camera that will never be
    /// watched this way - and not as the sidecar failing.
    /// </summary>
    [Fact]
    public async Task AnOfferForAJpegCameraIsACodecRefusal()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, JpegSource, TestContext.Current.CancellationToken);

        WebRtcOffer answer = await Client.OfferAsync(stream, BrowserOffer, TestContext.Current.CancellationToken);

        answer.Outcome.Should().Be(WebRtcOfferOutcome.CodecUnsupported);
    }

    /// <summary>A Motion-JPEG camera's stream gives Homespool's relay a whole first frame.</summary>
    [Fact]
    public async Task AJpegCamerasStreamGivesAFirstFrame()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, JpegSource, TestContext.Current.CancellationToken);

        using LiveMjpegStream? live = await OpenStreamAsync(stream);

        live.Should().NotBeNull();
        live!.ContentType.Should().StartWith("multipart/x-mixed-replace");
    }

    /// <summary>
    /// A camera that is not there gives the relay no first frame, so the viewer is told so rather
    /// than handed an empty stream - whatever status the sidecar wraps that in.
    /// </summary>
    [Fact]
    public async Task AnAbsentCamerasStreamGivesNoFirstFrame()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, AbsentSource, TestContext.Current.CancellationToken);

        using LiveMjpegStream? live = await OpenStreamAsync(stream);

        live.Should().BeNull();
    }

    /// <summary>
    /// A configuration write is merged: the address is in the document afterwards, and every
    /// registered stream survives it and the restart that makes the address take effect.
    /// </summary>
    [Fact]
    public async Task AConfigurationWriteKeepsTheStreamsThroughARestart()
    {
        RequireSidecar();
        Guid stream = Guid.NewGuid();
        await Client.PutStreamAsync(stream, H264Source, TestContext.Current.CancellationToken);

        const string Candidate = "192.0.2.10:8555";
        (await Client.WriteConfigAsync(
             $$$"""{"webrtc":{"candidates":["{{{Candidate}}}"],"ice_servers":[]}}""",
             TestContext.Current.CancellationToken)).Should().BeTrue();
        (await Client.RestartAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        IReadOnlySet<string>? listed = await ListedAfterRestartAsync();
        listed.Should().NotBeNull("the sidecar must come back from the restart");
        listed.Should().Contain(Name(stream), "a stream is part of the document the write merged into");
        (await Client.ReadConfigAsync(TestContext.Current.CancellationToken)).Should().Contain(Candidate);
    }

    /// <summary>
    /// With no device the sidecar may open, there are no devices - an answer, not a failure to ask.
    /// </summary>
    [Fact]
    public async Task WithoutADeviceGrantThereAreNoDevices()
    {
        RequireSidecar();

        (await Client.ListDeviceFormatsAsync(TestContext.Current.CancellationToken)).Should().NotBeNull().And.BeEmpty();
    }

    private static string Name(Guid stream)
    {
        return stream.ToString("D", CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlySet<string>> ListedAsync()
    {
        IReadOnlySet<string>? listed = await Client.ListStreamNamesAsync(TestContext.Current.CancellationToken);
        listed.Should().NotBeNull("the sidecar must answer a listing");

        return listed!;
    }

    /// <summary>The listing once the sidecar answers again, or null if it never does.</summary>
    private async Task<IReadOnlySet<string>?> ListedAfterRestartAsync()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);

            if (await Client.ListStreamNamesAsync(TestContext.Current.CancellationToken) is { } listed)
            {
                return listed;
            }
        }

        return null;
    }

    private async Task<CameraFrame?> FetchFrameAsync(Guid stream)
    {
        Uri frameUrl = Client.FrameUrl(stream) ?? throw new InvalidOperationException("the sidecar is configured");

        return await _services.GetRequiredService<ICameraSnapshotFetcher>()
                              .FetchAsync(frameUrl, TestContext.Current.CancellationToken);
    }

    private async Task<LiveMjpegStream?> OpenStreamAsync(Guid stream)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        return await _services.GetRequiredService<CameraStreamRelay>().OpenAsync(stream, deadline.Token);
    }

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string pathAndQuery)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(method, new Uri(BaseAddress, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim();
    }
}
