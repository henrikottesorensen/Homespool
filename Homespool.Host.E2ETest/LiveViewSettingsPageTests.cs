using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The live-view settings page: who may reach it, and what turning STUN on says before it does it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prompt is the feature.</b> Switching a boolean is trivial; what this page exists for is
/// that the two consequences are named before somebody chooses — your public address goes into every
/// offer, and a third party is contacted to discover it. Neither is visible from the outcome, so a
/// page that simply flipped it would be doing something on the operator's behalf that they never
/// agreed to. The assertions below are therefore about the words as much as the state.
/// </para>
/// <para>
/// No stream server runs here, so the choice is recorded and cannot be applied — which is itself one
/// of the cases, and the one that must not lose the choice.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class LiveViewSettingsPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-liveview-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// It decides whether this deployment contacts anyone outside itself, which is not any one team's
    /// call — the same reasoning that puts an attached camera behind administrator.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryAccountCannotReachIt()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "ordinary@example.com");

        using HttpResponseMessage response = await client.GetAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);

        client.Dispose();
    }

    /// <summary>
    /// Off is the state a deployment has without anyone choosing it, and the page has to say so
    /// rather than leave it to be inferred from an unticked box.
    /// </summary>
    [Fact]
    public async Task StunIsOffUntilSomebodyTurnsItOn()
    {
        HttpClient admin = await AdminAsync("stun-off@example.com");

        string page = await admin.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        page.Should().Contain("STUN off");
        page.Should().Contain("Allow STUN");

        admin.Dispose();
    }

    /// <summary>
    /// The two conditions, named, before anything happens. This is the whole reason the setting is a
    /// page rather than a line in <c>.env</c>.
    /// </summary>
    [Fact]
    public async Task TheConfirmationNamesBothConsequencesAndTheServer()
    {
        HttpClient admin = await AdminAsync("stun-prompt@example.com");

        string page = await admin.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        page.Should().Contain("Your public address is placed in every offer",
                              "a reflexive candidate carries it, and that is inherent rather than a quirk");
        page.Should().Contain("stops being self-contained");
        page.Should().Contain("stun.l.google.com",
                              "naming the server is the difference between a warning and a shrug");

        admin.Dispose();
    }

    /// <summary>
    /// The stream server cannot be reached here, and the choice must survive that: the startup path
    /// reads the database rather than asking the sidecar what was wanted, so a change made while it
    /// was down is applied at the next start instead of being lost.
    /// </summary>
    [Fact]
    public async Task AChoiceIsRecordedEvenWhenItCannotBeApplied()
    {
        HttpClient admin = await AdminAsync("stun-on@example.com");

        using HttpResponseMessage response = await PostStunAsync(admin, enabled: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        DeploymentSetting? settings = await database.DeploymentSettings.FirstOrDefaultAsync(
            TestContext.Current.CancellationToken);

        settings.Should().NotBeNull();
        settings!.WebRtcStunEnabled.Should().BeTrue();

        admin.Dispose();
    }

    /// <summary>
    /// Turning it back off has no prompt of its own, and nothing about it may be harder than turning
    /// it on: a confirmation on the safe direction is how people learn to click through the other.
    /// </summary>
    [Fact]
    public async Task ItCanBeTurnedBackOff()
    {
        HttpClient admin = await AdminAsync("stun-off-again@example.com");

        using (HttpResponseMessage on = await PostStunAsync(admin, enabled: true))
        {
            on.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (HttpResponseMessage off = await PostStunAsync(admin, enabled: false))
        {
            off.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        DeploymentSetting settings = await database.DeploymentSettings.FirstAsync(
            TestContext.Current.CancellationToken);

        settings.WebRtcStunEnabled.Should().BeFalse();

        admin.Dispose();
    }

    /// <summary>
    /// The role has to be granted before the cookie is minted, or the ticket carries an ordinary
    /// user and the page refuses with no explanation.
    /// </summary>
    private async Task<HttpClient> AdminAsync(string email)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email, AdminBootstrap.AdminRole);

        return client;
    }

    private async Task<HttpResponseMessage> PostStunAsync(HttpClient client, bool enabled)
    {
        string page = await client.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = enabled ? "true" : "false",
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        return await client.PostAsync("/Admin/LiveView", form, TestContext.Current.CancellationToken);
    }
}
