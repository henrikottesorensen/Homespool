using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Xunit.Abstractions;

using Homespool.Host.Listeners;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The listener boundary asserted against the application's real endpoint list, rather than against
/// a rule in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test is the reason the boundary is a boundary.</b> The spike that proved the two-listener
/// design also measured how it breaks: segregation is applied per mapping, so a route mapped without
/// it appears on <i>both</i> listeners, answering 200 on each, with no error and no warning. Nothing
/// about the site looks wrong afterwards - a leaked printer token simply reaches the application
/// surface, and a browser reaches <c>/p/ws</c>.
/// </para>
/// <para>
/// <c>notes/housekeeping.md</c> records this repository declaring a rule and never enforcing it four
/// separate times. So the rule is enumerated here against every endpoint the application actually
/// publishes: nothing is asserted about the mechanism, only about the outcome, which is what a future
/// change to either would have to keep true.
/// </para>
/// <para>
/// <b>Mutation-checked</b>: dropping <c>SegregateByListener</c> from the <c>MapControllers</c> call
/// fails <see cref="EveryRouteBelongsToExactlyTheListenerItsPrefixSays"/> and nothing else in this
/// file - the behavioural tests below keep passing, because the middleware's path fallback still keeps
/// <c>/p/*</c> off the user listener. That is the layering working as intended, and the reason the
/// enumeration test exists as well as the behavioural ones: it is the only thing that reports the
/// mistake while the runtime is quietly covering for it.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class RouteListenerSegregationTests : IAsyncLifetime, IDisposable
{
    /// <summary>
    /// <c>MapStaticAssets</c>' own file fallback, which it adds outside the convention builder it
    /// returns - so it is the one endpoint this test cannot expect to be classified.
    /// </summary>
    private const string StaticAssetFallbackPattern = "{**path:file}";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-listeners-{Guid.NewGuid():N}.db");
    private readonly ITestOutputHelper _output;
    private HomespoolFactory _factory = null!;

    public RouteListenerSegregationTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

    /// <summary>
    /// Every route under <c>/p</c> is printer-only, every other route is user-only, and no route is
    /// unclassified.
    /// </summary>
    [Fact]
    public void EveryRouteBelongsToExactlyTheListenerItsPrefixSays()
    {
        // Arrange - the endpoint list is only built once the application has started.
        using HttpClient started = _factory.CreateClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        List<string> wrong = [];
        List<string> unreachableByConvention = [];

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            string pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            ListenerRequirement? requirement = endpoint.Metadata.GetMetadata<ListenerRequirement>();
            ListenerClass expected = ListenerSegregation.ClassFor(pattern);

            _output.WriteLine($"{pattern,-60} {requirement?.Listener.ToString() ?? "NONE",-8} {endpoint.DisplayName}");

            if (requirement is null && pattern == StaticAssetFallbackPattern)
            {
                unreachableByConvention.Add(pattern);

                continue;
            }

            if (requirement is null || requirement.Listener != expected)
            {
                wrong.Add($"/{pattern} is {requirement?.Listener.ToString() ?? "unclassified"}, expected {expected}");
            }
        }

        // Assert
        endpoints.Endpoints.Should().NotBeEmpty("an empty endpoint list would make this test vacuous");

        wrong.Should().BeEmpty(
            "every endpoint must be mapped through SegregateByListener - a Map... call that misses it "
            + "puts its routes on both listeners at once, silently");

        unreachableByConvention.Should().HaveCountLessThanOrEqualTo(1,
            "the file fallback is the one endpoint the framework adds outside the builder MapStaticAssets "
            + "returns, so no convention of ours can classify it. It is covered instead by the middleware's "
            + "path fallback, and it is a user-side path. A second exemption appearing here means something "
            + "new is escaping the convention and needs looking at rather than adding to this list");
    }

    /// <summary>
    /// The printer protocol is absent from the user listener, which is where a reverse proxy delivers
    /// requests and where forwarded headers are trusted.
    /// </summary>
    [Fact]
    public async Task ThePrinterProtocolDoesNotExistOnTheUserListener()
    {
        // Arrange
        using HttpClient user = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await user.GetAsync("/p/register");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a printer endpoint reached through the proxy is not a printer");
    }

    /// <summary>
    /// And the application is absent from the printer listener, so a leaked printer token reaches no
    /// application surface at all - the point of splitting them.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/printers")]
    [InlineData("/health")]
    [InlineData("/setup")]
    [InlineData("/Account/Login")]
    public async Task TheApplicationDoesNotExistOnThePrinterListener(string path)
    {
        // Arrange
        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpResponseMessage response = await printer.GetAsync(path);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The same request on the listener it belongs to is served, so the 404s above are the boundary
    /// working rather than the route being broken.
    /// </summary>
    [Fact]
    public async Task TheSameRoutesAnswerOnTheirOwnListeners()
    {
        // Arrange
        using HttpClient user = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpResponseMessage health = await user.GetAsync("/health");
        using HttpResponseMessage register = await printer.GetAsync("/p/register");

        // Assert
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        register.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the endpoint exists here - it refuses a poll with no code, which is not the same thing");
    }
}
