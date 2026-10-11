using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

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

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();
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
        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
        _factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = Candidate;

        if (!reachable)
        {
            // Refused at once, so the startup configurer gives up immediately rather than at its deadline.
            _factory.ConfigurationOverrides["Cameras:StreamServerBaseUrl"] = "http://127.0.0.1:1";
        }

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        // STUN is left to the settings file rather than set by an override here: an override is
        // layered above the file, and no save could then change what it says.
        return _factory;
    }

    private static void Save(HomespoolFactory factory, Dictionary<string, string?> values)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<SettingsStore>().Save(values).Saved.Should().BeTrue();
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
