using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
public sealed class WebRtcLiveViewTests : IDisposable
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
                                  Options.Create(new CameraOptions()),
                                  NullLogger<Go2RtcClient>.Instance);

        WebRtcOffer answer = await client.OfferAsync(Guid.NewGuid(), "v=0", CancellationToken.None);

        answer.Outcome.Should().Be(WebRtcOfferOutcome.Failed);
        answer.Sdp.Should().BeNull();
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    /// <summary>
    /// A network camera's address says nothing about its codec, and nothing can report it in advance —
    /// measured on the board 2026-08-18: the stream server names no codec for a stream nobody is
    /// consuming, and a still fetch closes the producer the instant the frame is served. So it is
    /// offered and learned from.
    /// </summary>
    [Fact]
    public void ANetworkCameraIsOfferedLiveViewUntilItRefuses()
    {
        WebRtcAvailability availability = new() { Candidate = "192.168.13.183:8555" };
        Guid camera = Guid.NewGuid();

        availability.CanOffer(camera, "rtsp://192.168.13.217/live")
                    .Should().BeTrue("nothing can say otherwise until somebody tries");

        availability.MarkUnsupported(camera);

        availability.CanOffer(camera, "rtsp://192.168.13.217/live").Should().BeFalse(
            "a JPEG camera is refused permanently, so the button must not come back and fail again");
    }

    /// <summary>
    /// The half that needs no attempt at all. This source was composed by Homespool's own camera
    /// picker, which states the format rather than inheriting it - so a button here would be one
    /// nobody could ever use.
    /// </summary>
    [Fact]
    public void AnAttachedJpegCameraIsNeverOffered()
    {
        WebRtcAvailability availability = new() { Candidate = "192.168.13.183:8555" };

        availability.CanOffer(
            Guid.NewGuid(),
            "ffmpeg:device?video=/dev/v4l/by-id/usb-046d_0821_437242E0-video-index0&input_format=mjpeg")
                    .Should().BeFalse();
    }

    [Theory]
    [InlineData("ffmpeg:device?video=/dev/video0&input_format=mjpeg", true)]
    [InlineData("rtsp://camera.lan/live#video=mjpeg", true)]
    [InlineData("rtsp://camera.lan/live#video=jpeg", true)]
    [InlineData("rtsp://192.168.13.217/live", false)]
    [InlineData("onvif://user:pass@192.168.1.50", false)]
    [InlineData("http://camera.lan/snapshot.jpg", false)]
    [InlineData("rtsp://camera.lan/h264Preview_01_main", false)]
    [InlineData("", false)]
    public void OnlyASourceThatStatesJpegIsRefusedInAdvance(string source, bool expected)
    {
        WebRtcAvailability.DeclaresJpeg(source).Should().Be(expected);
    }

    /// <summary>
    /// The refusal is about one camera, not about the deployment. An MJPEG webcam beside an H.264
    /// camera must not take the button away from its neighbour.
    /// </summary>
    [Fact]
    public void OneCamerasRefusalDoesNotSilenceAnother()
    {
        WebRtcAvailability availability = new() { Candidate = "192.168.13.183:8555" };
        Guid webcam = Guid.NewGuid();
        Guid networkCamera = Guid.NewGuid();

        availability.MarkUnsupported(webcam);

        availability.CanOffer(networkCamera, "rtsp://192.168.13.217/live").Should().BeTrue();
    }

    /// <summary>
    /// With no address, nothing is offered whatever the camera can do — there would be nowhere to
    /// send the video.
    /// </summary>
    [Fact]
    public void NothingIsOfferedWithoutAnAddress()
    {
        new WebRtcAvailability().CanOffer(Guid.NewGuid(), "rtsp://192.168.13.217/live").Should().BeFalse();
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
        WebRtcAvailability availability = new() { Candidate = candidate };

        WebRtcCandidateHealthCheck check = new(
            availability,
            Options.Create(new CameraOptions { WebRtcCandidate = configured }),
            Options.Create(new PrusaConnectOptions { PrinterHost = printerHost }),
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
