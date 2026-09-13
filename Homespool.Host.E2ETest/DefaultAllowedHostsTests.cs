using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A production host started with nothing but its shipped configuration refuses a <c>Host</c> nobody
/// named.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters without a proxy in front.</b> Outgoing mail links are built from <c>Host</c>, so
/// a host that accepts any value lets the sender of a password-reset request choose where the link
/// points. Compose always supplies the list; these tests cover the run that has no compose to do it.
/// </para>
/// <para>
/// <b>Why the environment is set explicitly.</b> <c>WebApplicationFactory</c> runs as Development,
/// which loads <c>appsettings.Development.json</c> and its wildcard. Left there, the refusal below
/// would fail rather than pass - so it also proves the environment really changed.
/// </para>
/// </remarks>
public sealed class DefaultAllowedHostsTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("allowed-hosts");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);
        _factory = _root.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task AHostNobodyNamedIsRefused()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await GetLivenessAsync(client, "attacker.example");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                                        "the shipped list names only localhost, and the framework's host filter refuses anything else");
    }

    [Fact]
    public async Task LocalhostIsAnswered()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await GetLivenessAsync(client, "localhost");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
                                        "localhost is the floor compose also keeps, and what a probe on the machine itself uses");
    }

    /// <summary>
    /// <c>GET /health/live</c> with the given <c>Host</c> and no port, so the request stays on the user
    /// listener. Liveness answers before setup and without credentials, so a refusal can only be the
    /// host filter's.
    /// </summary>
    private static async Task<HttpResponseMessage> GetLivenessAsync(HttpClient client, string host)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Host = host;

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
