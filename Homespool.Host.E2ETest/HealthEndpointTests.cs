using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

using Homespool.Host.E2ETest;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Drives <c>/health</c> through the real pipeline, which is the only place the things most likely
/// to break it actually live: the setup gate and HTTPS redirection both sit in front of it, and
/// neither is visible from a unit test of the health check itself.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> call <c>SetupState.MarkComplete</c>. A monitoring probe hits a
/// freshly deployed container long before anyone has created the first administrator, and
/// <c>SetupGateMiddleware</c> redirects every navigable path to <c>/setup</c> until they have. If
/// <c>/health</c> were caught by that, a new deployment would answer probes with a 302 - which
/// <c>curl --fail</c> treats as success, so the container would report healthy without the health
/// check ever running.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class HealthEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-health-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
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

    [Fact]
    public async Task HealthIsServedBeforeSetupCompletesAndWithoutCredentials()
    {
        // Arrange - no redirects followed, so a 302 to /setup fails loudly instead of being papered
        // over by the handler quietly fetching the setup page and returning its 200.
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a monitoring probe has no credentials and arrives before the first administrator exists");
    }

    [Fact]
    public async Task HealthReportsTheWriterStateAsJson()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using JsonDocument document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy",
            "nothing has failed to flush on a freshly started host");

        JsonElement check = document.RootElement.GetProperty("checks")[0];
        check.GetProperty("name").GetString().Should().Be("telemetry-persistence");

        // The counters are the point: a bare status word cannot distinguish a database that is
        // briefly stuck from one that has already lost events.
        check.GetProperty("data").TryGetProperty("pendingSamples", out _).Should().BeTrue();
        check.GetProperty("data").TryGetProperty("discardedEvents", out _).Should().BeTrue();
    }

    /// <summary>
    /// <c>/health/live</c> reports only the liveness check, never persistence.
    /// </summary>
    /// <remarks>
    /// The filtering is the safety property, not a detail. Anything that can kill the container - a
    /// Kubernetes livenessProbe, a Swarm healthcheck, an autoheal sidecar - is meant to point here,
    /// and a restart does not fix a database that is rejecting writes; it discards everything still
    /// buffered. If the persistence check ever leaked into this endpoint through a stray tag, a
    /// recoverable outage would become a restart loop that destroys telemetry on every cycle, and
    /// nothing else would notice.
    /// </remarks>
    [Fact]
    public async Task LivenessReportsOnlyTheDrainLoopNotPersistence()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        HttpResponseMessage response = await client.GetAsync("/health/live");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement checks = document.RootElement.GetProperty("checks");

        checks.GetArrayLength().Should().Be(1, "only checks tagged for liveness belong here");
        checks[0].GetProperty("name").GetString().Should().Be("telemetry-writer-alive");

        body.Should().NotContain("telemetry-persistence",
            "persistence health must never be able to trigger a restart");
    }

    /// <summary>
    /// Pages still render with the health banner in the layout, and show nothing when healthy.
    /// </summary>
    /// <remarks>
    /// The unit tests cover which problems become banners; this covers the half they cannot - that
    /// the view component resolves its dependency and finds its view at all. Both failures throw
    /// during rendering of <c>_Layout</c>, which means every page in the application, so getting one
    /// page back at all is the assertion that matters.
    /// </remarks>
    [Fact]
    public async Task PagesRenderWithTheHealthBannerAndShowNothingWhenHealthy()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act - /setup is the one page reachable before an administrator exists, and it renders the
        // shared layout like any other.
        HttpResponseMessage response = await client.GetAsync("/setup");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a view component that cannot resolve its dependency or its view takes every page down with it");

        body.Should().NotContain("Service problem",
            "nothing has failed on a freshly started host, and a healthy service should look untouched");
    }
}
