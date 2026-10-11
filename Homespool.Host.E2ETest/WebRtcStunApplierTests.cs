using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Host.Configuration;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A STUN choice saved while Homespool runs reaches the stream server then, not at the next restart
/// - and the live-view page says so only once the stream server has it.
/// </summary>
/// <remarks>
/// The fake sidecar keeps its streams across a restart, so the reconcile that follows a write is
/// seen by what it asks: the stream listing that opens every pass.
/// </remarks>
public sealed class WebRtcStunApplierTests : IAsyncLifetime
{
    private const string Candidate = "192.0.2.10:8555";

    private const string NotYetApplied = "The stream server has not taken this choice yet.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("webrtc-stun-applier");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory? _factory;

    private string _marker = null!;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();
        _marker = Path.Combine(_scratch.Path, "stream-server-restart-owed");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task SwitchingStunOnReachesTheStreamServerWithoutRestartingHomespool()
    {
        HomespoolFactory factory = Start();

        int listingsBefore = Listings();

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });

        (await EventuallyAsync(() => _sidecar.Restarts == 2)).Should().BeTrue("the change restarts the stream server");
        IReadOnlyList<string> writes = _sidecar.ConfigWrites;
        writes.Should().HaveCount(2);
        writes[^1].Should().Contain("stun:stun.l.google.com:19302");

        (await EventuallyAsync(() => Listings() > listingsBefore)).Should().BeTrue(
            "every camera is reconciled after the write, in case one was dropped from the file");

        factory.Services.GetRequiredService<CameraLiveAvailability>().StunInForce
               .Should().Be(new WebRtcStunChoice(true, "stun:stun.l.google.com:19302"));
    }

    [Fact]
    public async Task SwitchingStunBackOffEmptiesTheStunServersAgain()
    {
        HomespoolFactory factory = Start();

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });
        (await EventuallyAsync(() => _sidecar.Restarts == 2)).Should().BeTrue();

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "false" });

        (await EventuallyAsync(() => _sidecar.Restarts == 3)).Should().BeTrue();
        _sidecar.ConfigWrites[^1].Should().Contain("\"ice_servers\":[]", "an empty list is what turns go2rtc's own default off");
    }

    /// <summary>
    /// Every save reloads every option, and a restart of the stream server drops whoever is watching
    /// - so a save that leaves the STUN choice as it was must not cost one, nor a pass over every
    /// camera. The server of a STUN that is off is not part of the choice.
    /// </summary>
    [Fact]
    public async Task ASaveThatLeavesTheChoiceAloneDoesNotRestartTheStreamServer()
    {
        HomespoolFactory factory = Start();

        int listingsBefore = Listings();

        Save(factory, new Dictionary<string, string?>
        {
            ["Invitations:LifetimeHours"] = "12",
            ["Cameras:WebRtcStunServer"] = "stun:elsewhere.example.test:3478",
        });

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        _sidecar.Restarts.Should().Be(1, "only the startup write restarted it; a server nobody contacts is not a change");
        _sidecar.ConfigWrites.Should().ContainSingle();
        Listings().Should().Be(listingsBefore, "nothing changed, so no camera needs writing back");
    }

    /// <summary>
    /// <b>A restart that fails leaves a restart owed, and a matching file does not settle it.</b> The
    /// write lands in the sidecar's file at once and in the running sidecar only at a restart, so a
    /// retry finding the file in agreement must restart rather than believe it - or STUN switched off
    /// would be reported off while the sidecar goes on contacting the STUN server.
    /// </summary>
    [Fact]
    public async Task ARefusedRestartIsOwedRatherThanReportedAsApplied()
    {
        HomespoolFactory factory = Start();
        CameraLiveAvailability availability = factory.Services.GetRequiredService<CameraLiveAvailability>();

        _sidecar.RefuseRestarts(true);

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });

        (await EventuallyAsync(() => _sidecar.RefusedRestarts >= 1)).Should().BeTrue();
        availability.StunInForce.Should().BeNull("with a restart owed, what the running sidecar holds is not known");
        File.Exists(_marker).Should().BeTrue("the owed restart has to outlive Homespool restarting");

        _sidecar.RefuseRestarts(false);

        // Any save wakes the applier at once, rather than at the end of its retry interval.
        Save(factory, new Dictionary<string, string?> { ["Invitations:LifetimeHours"] = "12" });

        (await EventuallyAsync(() => _sidecar.Restarts == 2)).Should().BeTrue("the file already agreed, and is restarted rather than believed");
        _sidecar.ConfigWrites.Should().HaveCount(2, "nothing needed writing again");
        availability.StunInForce.Should().Be(new WebRtcStunChoice(true, "stun:stun.l.google.com:19302"));
        File.Exists(_marker).Should().BeFalse("the restart it recorded has been made");
    }

    /// <summary>
    /// <b>Switching back while a restart is owed is written, not taken as already done.</b> The
    /// unconfirmed restart may have happened - go2rtc can drop the connection while restarting - so the
    /// sidecar may already run the choice just written; the choice it ran before is no longer known to
    /// be in force, and switching back to it must still reach the file.
    /// </summary>
    [Fact]
    public async Task SwitchingBackWhileARestartIsOwedIsStillWritten()
    {
        HomespoolFactory factory = Start();

        _sidecar.RefuseRestarts(true);

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });
        (await EventuallyAsync(() => _sidecar.RefusedRestarts >= 1)).Should().BeTrue();

        Save(factory, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "false" });

        (await EventuallyAsync(() => _sidecar.ConfigWrites.Count == 3)).Should().BeTrue(
            "the file says STUN is on, and the sidecar may be running it");
        _sidecar.ConfigWrites[^1].Should().Contain("\"ice_servers\":[]");
    }

    /// <summary>
    /// <b>A reconcile that fails is run again</b>, though the choice it followed is by then in force -
    /// a stream the configuration write dropped would otherwise stay missing until its camera is next
    /// saved.
    /// </summary>
    [Fact]
    public async Task AReconcileThatFailedIsRunAgainOnTheNextPass()
    {
        HomespoolFactory root = Configure();
        bool failing = true;

        await using WebApplicationFactory<Controllers.PrinterAppController> factory = root.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(services =>
                services.AddScoped(provider => failing ?
                    throw new InvalidOperationException("The database is unavailable.") :
                    ActivatorUtilities.CreateInstance<CameraStreamSweeper>(provider))));

        _ = factory.Server;

        Save(factory.Services, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });
        (await EventuallyAsync(() => _sidecar.Restarts == 2)).Should().BeTrue();

        // The reconcile after the write lists the streams before it fails, so the listing counts it.
        int listingsAfterTheFailure = await SettledListingsAsync();

        failing = false;

        // Any save wakes the applier at once, rather than at the end of its retry interval.
        Save(factory.Services, new Dictionary<string, string?> { ["Invitations:LifetimeHours"] = "12" });

        (await EventuallyAsync(() => Listings() > listingsAfterTheFailure)).Should().BeTrue(
            "the reconcile is owed until one completes");
    }

    /// <summary>
    /// <b>A failure during a pass costs a retry, never the server.</b> An exception leaving a
    /// background service stops the whole host by default; here every reconcile fails, at startup and
    /// after each change, and Homespool both keeps running and goes on applying the next change.
    /// </summary>
    [Fact]
    public async Task AFailingReconcileNeitherStopsHomespoolNorTheNextChange()
    {
        HomespoolFactory root = Configure();

        await using WebApplicationFactory<Controllers.PrinterAppController> factory = root.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(services =>
                services.AddScoped<CameraStreamSweeper>(_ => throw new InvalidOperationException("The database is unavailable."))));

        _ = factory.Server;

        IHostApplicationLifetime lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        Save(factory.Services, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "true" });
        (await EventuallyAsync(() => _sidecar.Restarts == 2)).Should().BeTrue();

        Save(factory.Services, new Dictionary<string, string?> { ["Cameras:WebRtcStunEnabled"] = "false" });
        (await EventuallyAsync(() => _sidecar.Restarts == 3)).Should().BeTrue("the applier survived the failure after the first change");

        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse("no failed reconcile may stop the host");
    }

    /// <summary>
    /// A stream server that never confirmed the choice is not reported as holding it - turning STUN
    /// off is a privacy choice, and the page saying so before it has happened would be the lie.
    /// </summary>
    [Fact]
    public async Task ThePageSaysWhenTheStreamServerHasNotTakenTheChoice()
    {
        HomespoolFactory factory = Start(reachable: false);

        HttpClient admin = await AdminAsync(factory, "stun-pending@example.com");

        string page = await admin.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        page.Should().Contain(WebUtility.HtmlEncode(NotYetApplied));

        admin.Dispose();
    }

    [Fact]
    public async Task ThePageSaysNothingMoreOnceTheStreamServerHasIt()
    {
        HomespoolFactory factory = Start();

        HttpClient admin = await AdminAsync(factory, "stun-applied@example.com");

        string page = await admin.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        page.Should().NotContain(WebUtility.HtmlEncode(NotYetApplied));

        admin.Dispose();
    }

    private HomespoolFactory Start(bool reachable = true)
    {
        Configure(reachable);

        _ = _factory!.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        // STUN is left to the settings file rather than set by an override here: an override is
        // layered above the file, and no save could then change what it says.
        return _factory;
    }

    /// <summary>A host against the sidecar, not yet started.</summary>
    private HomespoolFactory Configure(bool reachable = true)
    {
        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
        _factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = Candidate;
        _factory.ConfigurationOverrides["Cameras:RestartOwedMarker"] = _marker;

        if (!reachable)
        {
            // Refused at once, so the startup configurer gives up immediately rather than at its deadline.
            _factory.ConfigurationOverrides["Cameras:StreamServerBaseUrl"] = "http://127.0.0.1:1";
        }

        return _factory;
    }

    private static void Save(HomespoolFactory factory, Dictionary<string, string?> values)
    {
        Save(factory.Services, values);
    }

    private static void Save(IServiceProvider services, Dictionary<string, string?> values)
    {
        using IServiceScope scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<SettingsStore>().Save(values).Saved.Should().BeTrue();
    }

    /// <summary>The listing count once it has stopped moving for half a second.</summary>
    private async Task<int> SettledListingsAsync()
    {
        int seen = Listings();

        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            int now = Listings();

            if (now == seen)
            {
                return now;
            }

            seen = now;
        }
    }

    private int Listings()
    {
        return _sidecar.Requests.Count(request => request.StartsWith("GET /api/streams", StringComparison.Ordinal));
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    private static async Task<HttpClient> AdminAsync(HomespoolFactory factory, string email)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            factory, email, AdminBootstrap.AdminRole);

        await EnrolmentFlowHelper.ReauthenticateAsync(client);

        return client;
    }
}
