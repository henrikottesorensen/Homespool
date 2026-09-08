using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Every printer route carries a rate-limit policy, asserted against the application's real endpoint
/// list rather than against the attributes anyone remembered to write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this catches is an omission, which is why it has to be an enumeration.</b>
/// <c>GET /p/teams/{teamId}/files/{hash}/raw</c> shipped with no policy and nothing said so: a
/// missing attribute is indistinguishable from an action nobody has annotated yet, and the route
/// answered normally throughout. What made it matter is that every route here runs the printer
/// authentication handler, which spends a PBKDF2 verifying the presented token - so an unmetered
/// printer route sells hashing at wire rate whatever the action itself does.
/// </para>
/// <para>
/// The rate limiter runs before authentication, deliberately, so a policy is the only thing between
/// an anonymous caller and that work. Same reasoning as
/// <see cref="RouteListenerSegregationTests"/>: assert the outcome over every published endpoint,
/// not the mechanism that is supposed to produce it.
/// </para>
/// <para>
/// <b>Coverage, where <see cref="PrinterRateLimitTests"/> is behaviour.</b> Those drive real requests
/// to show the window and the ceiling do what they claim; these ask whether a route was wired to them
/// at all. Neither substitutes for the other - a route with no policy passes every behavioural test
/// in the suite, because none of them ask it for anything.
/// </para>
/// <para>
/// <b>Mutation-checked</b> by removing <c>[EnableRateLimiting]</c> from the controller: the raw-fetch
/// route reappears here as unmetered, and nothing else in the suite notices.
/// </para>
/// </remarks>
public sealed class PrinterRouteRateLimitTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printer-rate-limits");
    private readonly ITestOutputHelper _output;
    private HomespoolFactory _factory = null!;

    public PrinterRouteRateLimitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public void EveryPrinterRouteCarriesARateLimitPolicy()
    {
        // Arrange - the endpoint list is only built once the application has started.
        using HttpClient started = _factory.CreateClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        List<string> printerRoutes = [];
        List<string> unmetered = [];

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            string pattern = endpoint.RoutePattern.RawText ?? string.Empty;

            if (!pattern.StartsWith("p/", StringComparison.Ordinal))
            {
                continue;
            }

            printerRoutes.Add(pattern);

            // The attribute's metadata, not the policy's behaviour: what is being asserted is that
            // somebody decided, and DisableRateLimiting counts as a decision only when it is written
            // down - which is the whole reason the default sits on the controller.
            EnableRateLimitingAttribute? policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
            DisableRateLimitingAttribute? disabled = endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>();

            _output.WriteLine($"/{pattern,-45} {policy?.PolicyName ?? (disabled is not null ? "DISABLED" : "NONE")}");

            if (policy is null && disabled is null)
            {
                unmetered.Add($"/{pattern}");
            }
        }

        // Assert
        printerRoutes.Should().NotBeEmpty("an empty printer surface would make this test vacuous");

        unmetered.Should().BeEmpty(
            "every /p/* route reaches the printer authentication handler and its PBKDF2 before anything "
            + "has been authenticated, so one without a policy is an unmetered way to buy that work. The "
            + "controller-level default covers new actions; an action that genuinely wants none has to "
            + "say [DisableRateLimiting] and be seen doing it");
    }

    /// <summary>
    /// Every policy a printer route names is one this class declares - the tripwire for a sixth
    /// policy arriving wired to only half of the limiter.
    /// </summary>
    /// <remarks>
    /// A policy needs an <c>AddPolicy</c> for its per-printer window <b>and</b> an arm in the
    /// ceiling's switch, and nothing makes the pair. This cannot see the wiring, so it asserts the
    /// next best thing: that the set of policies actually in use is the set somebody has thought
    /// about. A legitimate sixth fails here once, which is the prompt to check both sites.
    /// </remarks>
    [Fact]
    public void EveryPrinterRoutesPolicyIsOneTheLimiterDeclares()
    {
        // Arrange
        using HttpClient started = _factory.CreateClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        string[] declared =
        [
            PrinterRateLimits.RegistrationStartPolicy,
            PrinterRateLimits.RegistrationPollPolicy,
            PrinterRateLimits.SocketPolicy,
            PrinterRateLimits.HttpTransportPolicy,
            PrinterRateLimits.FilePolicy,
        ];

        // Act
        List<string> inUse = endpoints.Endpoints
                                      .OfType<RouteEndpoint>()
                                      .Where(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("p/", StringComparison.Ordinal))
                                      .Select(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName)
                                      .Where(name => name is not null)
                                      .Select(name => name!)
                                      .Distinct()
                                      .ToList();

        // Assert
        inUse.Should().NotBeEmpty();
        inUse.Should().BeSubsetOf(declared,
                                  "a policy needs both an AddPolicy for its per-printer window and an arm in the "
                                  + "ceiling's switch, and nothing pairs them - so a new one arriving here is the "
                                  + "moment to check it was wired to both");
    }

    /// <summary>
    /// The file route's ceiling is wired, not just its policy - a caller rotating fingerprints meets
    /// it rather than running free.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the two tests above cannot see the wiring, and a mutant proved it.</b>
    /// Half-wiring a policy left the route annotated, the policy declared and the per-printer window
    /// working - so every metadata assertion passed - while the route had no ceiling at all, which is
    /// exactly what a caller minting a fresh fingerprint per request walks through. Only a request can
    /// tell. Driven the same way as
    /// <see cref="PrinterRateLimitTests.TheCeilingBoundsACallerRotatingFingerprints"/>, whose
    /// reasoning about counting off the constants applies here too.
    /// </remarks>
    [Fact]
    public async Task TheFileRoutesCeilingBoundsACallerRotatingFingerprints()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        List<HttpStatusCode> answers = [];

        // Act - a hash nothing offered, so an admitted request is refused by authentication rather
        // than served; the limiter runs first, which is the whole point.
        for (int i = 0; i <= PrinterRateLimits.FileCeiling; i += 1)
        {
            using HttpRequestMessage fetch = new(HttpMethod.Get, "/p/teams/1/files/whatever/raw");
            fetch.Headers.TryAddWithoutValidation(Headers.Fingerprint, PrinterIdentity.CreateRandom().HeaderFingerprint);

            using HttpResponseMessage response = await printers.SendAsync(fetch, TestContext.Current.CancellationToken);
            answers.Add(response.StatusCode);
        }

        // Assert
        answers[..PrinterRateLimits.FileCeiling]
            .Should().AllSatisfy(status => status.Should().Be(HttpStatusCode.Unauthorized,
                                                              "a fingerprint nobody has used yet has its whole window, and an "
                                                              + "admitted request reaches authentication - the exact status matters, "
                                                              + "because a policy named but never registered answers 500 here and "
                                                              + "'not 429' would call that a pass"));

        answers[PrinterRateLimits.FileCeiling]
            .Should().Be(HttpStatusCode.TooManyRequests, "the ceiling counts every fingerprint together");
    }

    /// <summary>
    /// The route the omission was found on, named so a regression reports the specific thing rather
    /// than only the rule.
    /// </summary>
    [Fact]
    public void TheRawFetchRouteIsMetered()
    {
        // Arrange
        using HttpClient started = _factory.CreateClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint fetch = endpoints.Endpoints
                                       .OfType<RouteEndpoint>()
                                       .Single(e => e.RoutePattern.RawText == "p/teams/{teamId:long}/files/{hash}/raw");

        // Assert
        EnableRateLimitingAttribute? policy = fetch.Metadata.GetMetadata<EnableRateLimitingAttribute>();

        policy.Should().NotBeNull(
            "an anonymous caller with an unknown fingerprint reaches the provisioning scan through here");
        policy!.PolicyName.Should().Be(PrinterRateLimits.FilePolicy);
    }
}
