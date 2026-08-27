using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Cameras;
using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Live camera view: what is refused without a credential, and what an administrator is told when
/// there is no address to send video to.
/// </summary>
/// <remarks>
/// <b>The health check is most of this file because the failure it reports has no other symptom.</b>
/// With no address the live-view button never appears, which is indistinguishable from a feature
/// that was never built — nothing is logged repeatedly, nothing is broken, and the still keeps
/// working. So the sentence in the banner is the entire diagnosis, and which sentence it is decides
/// whether the operator checks the right setting.
/// </remarks>
public sealed class CameraLiveAvailabilityTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-webrtc-{Guid.NewGuid():N}.db");

    /// <summary>
    /// The same rule every other member of the client follows, and it has to hold here too:
    /// signalling is what hands a browser the credentials to receive media, so an uncredentialed
    /// sidecar must not be asked to do it either.
    /// </summary>
    [Fact]
    public async Task NoOfferIsSentWithoutACredential()
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        Go2RtcClient client = new(factory,
                                  TestOptions.Monitor(new CameraOptions()),
                                  NullLogger<Go2RtcClient>.Instance);

        WebRtcOffer answer = await client.OfferAsync(Guid.NewGuid(), "v=0", CancellationToken.None);

        answer.Outcome.Should().Be(WebRtcOfferOutcome.Failed);
        answer.Sdp.Should().BeNull();
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    /// <summary>
    /// The transport is decided from the codec the stream server reports, and go2rtc's own lists
    /// decide the mapping: its WebRTC consumer carries H264/H265/VP8/VP9/AV1, and its MJPEG stream
    /// carries JPEG alone - measured 2026-08-19, when an H.264 camera answered its multipart request
    /// with 200 and then silence.
    /// </summary>
    [Theory]
    [InlineData("JPEG", LiveTransport.Mjpeg)]
    [InlineData("H264", LiveTransport.Webrtc)]
    [InlineData("H265", LiveTransport.Webrtc)]
    [InlineData("HEVC-weirdness", LiveTransport.None)]
    public async Task TheCodecDecidesTheTransport(string codec, LiveTransport expected)
    {
        CameraLiveAvailability availability = new(ProbeAnswering(codec))
        {
            Candidate = "192.168.13.183:8555",
        };

        (await availability.HowToWatchAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(expected);
    }

    /// <summary>
    /// With no address, WebRTC is not offered whatever the camera can do - there would be nowhere to
    /// send the video. The MJPEG stream needs no address, because it travels through Homespool over
    /// the same HTTP the page came on.
    /// </summary>
    [Fact]
    public async Task WithoutAnAddressOnlyTheMjpegStreamIsOffered()
    {
        CameraLiveAvailability unconfigured = new(ProbeAnswering("H264"));

        (await unconfigured.HowToWatchAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().Be(LiveTransport.None);

        CameraLiveAvailability jpeg = new(ProbeAnswering("JPEG"));

        (await jpeg.HowToWatchAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().Be(LiveTransport.Mjpeg);
    }

    /// <summary>
    /// One probe per camera per process: the codec is a property of the hardware, and a DESCRIBE
    /// makes the stream server open the camera, so asking on every page load would keep it awake
    /// for nobody.
    /// </summary>
    [Fact]
    public async Task ACameraIsProbedOnceAndRemembered()
    {
        ICameraCodecProbe probe = ProbeAnswering("JPEG");
        CameraLiveAvailability availability = new(probe);
        Guid camera = Guid.NewGuid();

        await availability.HowToWatchAsync(camera, CancellationToken.None);
        await availability.HowToWatchAsync(camera, CancellationToken.None);

        await probe.Received(1).ProbeCodecsAsync(camera, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A probe that gets no answer means the camera is off, not that it cannot - so it is asked
    /// again, and the button appears when the camera comes back without anyone restarting anything.
    /// </summary>
    [Fact]
    public async Task ACameraThatDidNotAnswerIsAskedAgain()
    {
        ICameraCodecProbe probe = Substitute.For<ICameraCodecProbe>();
        Guid camera = Guid.NewGuid();

        probe.ProbeCodecsAsync(camera, Arg.Any<CancellationToken>())
             .Returns((IReadOnlySet<string>?)null, new HashSet<string> { "JPEG" });

        CameraLiveAvailability availability = new(probe);

        (await availability.HowToWatchAsync(camera, CancellationToken.None)).Should().Be(LiveTransport.None);
        (await availability.HowToWatchAsync(camera, CancellationToken.None)).Should().Be(LiveTransport.Mjpeg);
    }

    /// <summary>
    /// A source edit forgets the probed codec - a different camera can be put at the same address,
    /// and the remembered answer belongs to the hardware, not the row.
    /// </summary>
    [Fact]
    public async Task ForgettingACameraMakesTheNextAskProbeAgain()
    {
        ICameraCodecProbe probe = ProbeAnswering("JPEG");
        CameraLiveAvailability availability = new(probe);
        Guid camera = Guid.NewGuid();

        await availability.HowToWatchAsync(camera, CancellationToken.None);
        availability.Forget(camera);
        await availability.HowToWatchAsync(camera, CancellationToken.None);

        await probe.Received(2).ProbeCodecsAsync(camera, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The SDP grammar the probe relies on, against the answers the real sidecar gave on
    /// 2026-08-20 - and the audio section must not leak codecs into a video decision.
    /// </summary>
    [Fact]
    public void TheSdpParserReadsVideoCodecsAndOnlyThose()
    {
        const string sdp = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 120\r\n\r\n"
                           + "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\n"
                           + "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n"
                           + "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/16000\r\n";

        Go2RtcClient.ParseSdpVideoCodecs(sdp).Should().BeEquivalentTo(["H264"]);

        Go2RtcClient.ParseSdpVideoCodecs("m=video 0 RTP/AVP 26\r\na=rtpmap:26 JPEG/90000\r\n")
                    .Should().BeEquivalentTo(["JPEG"]);
    }

    /// <summary>
    /// The wire names are the page contract - camera-live.js switches on them - so a rename in the
    /// enum must fail here rather than as a button that silently does nothing.
    /// </summary>
    [Fact]
    public void TheTransportSerialisesToTheNamesThePageSwitchesOn()
    {
        System.Text.Json.JsonSerializer.Serialize(new CameraLiveOption(true, LiveTransport.Webrtc))
              .Should().Contain("\"webrtc\"");

        System.Text.Json.JsonSerializer.Serialize(new CameraLiveOption(true, LiveTransport.Mjpeg))
              .Should().Contain("\"mjpeg\"");
    }

    private static ICameraCodecProbe ProbeAnswering(string codec)
    {
        ICameraCodecProbe probe = Substitute.For<ICameraCodecProbe>();

        probe.ProbeCodecsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { codec });

        return probe;
    }

    [Fact]
    public async Task TheHealthCheckIsQuietWhenNoCameraIsConfigured()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        HealthCheckResult result = await CheckAsync(context, candidate: string.Empty);

        result.Status.Should().Be(HealthStatus.Healthy,
                                  "a deployment with no cameras has no use for an address, and a banner about one "
                                  + "is how people learn to ignore banners");
    }

    [Fact]
    public async Task TheHealthCheckIsQuietOnceAnAddressIsKnown()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(context, candidate: "192.168.13.183:8555");

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("192.168.13.183:8555");
    }

    /// <summary>
    /// Nothing configured at all, which is the ordinary way to arrive here: the address is derived
    /// from <c>PRINTER_HOST</c>, and a deployment that never set one has nothing to derive from.
    /// </summary>
    [Fact]
    public async Task TheHealthCheckAsksForAHostWhenNothingIsSet()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(context, candidate: string.Empty);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("PRINTER_HOST");

        // Said explicitly, because the point of the banner is that somebody does not go hunting for a
        // broken camera.
        result.Description.Should().Contain("Still pictures are unaffected");
    }

    /// <summary>
    /// A host that is set and resolves to nothing usable — a name pointing at a container address,
    /// most likely. Naming it is what stops an operator checking the setting they already set.
    /// </summary>
    [Fact]
    public async Task TheHealthCheckNamesAHostThatDidNotResolveUsefully()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(context, candidate: string.Empty, printerHost: "homespool.lan");

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("homespool.lan");
        result.Description.Should().Contain("WEBRTC_CANDIDATE");
    }

    /// <summary>
    /// An override that was set and could not be used. A different fault with a different fix, and
    /// the sentence has to say so rather than send them back to <c>PRINTER_HOST</c>.
    /// </summary>
    [Fact]
    public async Task TheHealthCheckNamesTheOverrideWhenOneWasSet()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(
            context, candidate: string.Empty, printerHost: "homespool.lan", configured: "not an address");

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("WEBRTC_CANDIDATE is set");
        result.Description.Should().NotContain("PRINTER_HOST",
                                               "an operator who set the override should not be sent to the setting "
                                               + "it overrides");
    }

    private static async Task<HealthCheckResult> CheckAsync(HomespoolDbContext context,
                                                            string candidate,
                                                            string printerHost = "",
                                                            string configured = "")
    {
        CameraLiveAvailability availability = new(Substitute.For<ICameraCodecProbe>()) { Candidate = candidate };

        WebRtcCandidateHealthCheck check = new(
            availability,
            TestOptions.Monitor(new CameraOptions { WebRtcCandidate = configured }),
            TestOptions.Monitor(new PrusaConnectOptions { PrinterHost = printerHost }),
            context);

        return await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    private static async Task AddCameraAsync(HomespoolDbContext context)
    {
        Team team = new() { Name = "Workshop", CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Cameras.Add(new Camera
        {
            Uuid = Guid.NewGuid(),
            Name = "Bed",
            Source = "rtsp://192.0.2.1/live",
            TeamId = team.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
