using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The live-view page after the STUN switch moved to the settings page: who may read it, and that it
/// still says what live viewing is doing.
/// </summary>
/// <remarks>
/// <b>The prompt this file used to assert on moved with the switch.</b> What it said - that your
/// public address goes into every offer, and that a third party is contacted to discover it - is now
/// the settings page's confirmation, and is asserted there. This page reports.
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
    /// It reports the STUN state rather than setting it, so somebody reading the page is not left
    /// wondering where the switch went.
    /// </summary>
    [Fact]
    public async Task ItShowsWhetherStunIsAllowedAndSaysWhereToChangeIt()
    {
        HttpClient admin = await AdminAsync("stun-shown@example.com");

        string page = await admin.GetStringAsync("/Admin/LiveView", TestContext.Current.CancellationToken);

        page.Should().Contain("STUN off", "off is the state a deployment has without anyone choosing it");
        page.Should().Contain("settings page", "the switch lives there now");
        page.Should().NotContain("Allow STUN", "and no longer here");

        admin.Dispose();
    }

    private async Task<HttpClient> AdminAsync(string email)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email, AdminBootstrap.AdminRole);

        return client;
    }
}
