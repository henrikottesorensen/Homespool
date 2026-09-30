using System;
using System.Net;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Middleware;
using Homespool.Host.PrusaConnect;
using Homespool.Host.RateLimiting;

namespace Homespool.Host.Test;

/// <summary>
/// The printer limiter's accounting: a caller's window and its route's ceiling, spent together or not
/// at all.
/// </summary>
/// <remarks>
/// Driven with the code-request route, whose window is per address and small - five under a ceiling
/// of 120 - so each property takes few requests to show. Every count is off the constants.
/// </remarks>
public sealed class PrinterRouteLimiterTests
{
    private const string Policy = RateLimitPolicies.PrinterRegistrationStart;

    private const int Window = PrinterRateLimits.RegistrationStartPerAddressLimit;

    private const int Ceiling = PrinterRateLimits.RegistrationStartCeiling;

    /// <summary>A host that trusts a proxy and keeps client addresses, so the window is per address.</summary>
    private static readonly IServiceProvider Services = BuildServices();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The finding this class exists for: a caller refused by its own window takes nothing from the
    /// ceiling, however often it asks and whichever way the middleware asks - so every other caller
    /// still has the ceiling, less the one window that was spent.
    /// </summary>
    [Fact]
    public async Task ACallerRefusedByItsOwnWindowSpendsNothingOfTheCeiling()
    {
        // Arrange
        using PrinterRouteLimiter limiter = new(_time);
        Admit(limiter, "203.0.113.7", Window).Should().Be(Window, "the window admits its permits");

        // Act - the middleware asks once, then again through the waiting path, for every request.
        for (int i = 0; i < Ceiling * 2; i += 1)
        {
            using RateLimitLease attempted = limiter.AttemptAcquire(Request("203.0.113.7"));
            using RateLimitLease waited = await limiter.AcquireAsync(Request("203.0.113.7"), cancellationToken: TestContext.Current.CancellationToken);
            attempted.IsAcquired.Should().BeFalse();
            waited.IsAcquired.Should().BeFalse();
        }

        // Assert
        AdmitOnceEach(limiter, Ceiling - Window, first: 1).Should().Be(Ceiling - Window, "the ceiling lost only what was admitted");
        AdmitOnceEach(limiter, 1, first: Ceiling).Should().Be(0, "and then it is full");
    }

    /// <summary>
    /// Callers with a fresh window each - an attacker rotating keys - are what the ceiling bounds.
    /// </summary>
    [Fact]
    public void CallersRotatingKeysMeetTheCeiling()
    {
        using PrinterRouteLimiter limiter = new(_time);

        AdmitOnceEach(limiter, Ceiling + 1, first: 1).Should().Be(Ceiling);
    }

    /// <summary>
    /// The reverse direction: a request the ceiling refuses takes nothing from its caller's window. A
    /// printer polling while somebody else fills the ceiling keeps its own count for when the ceiling
    /// rolls over. Shown by starting the caller's window half a minute after the ceiling's, so the
    /// ceiling rolls while the caller's window is still the same one.
    /// </summary>
    [Fact]
    public void ARequestTheCeilingRefusesSpendsNothingOfTheCallersWindow()
    {
        // Arrange
        using PrinterRouteLimiter limiter = new(_time);
        Admit(limiter, "198.51.100.1", 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        Admit(limiter, "203.0.113.7", 1).Should().Be(1);
        AdmitOnceEach(limiter, Ceiling - 2, first: 2).Should().Be(Ceiling - 2, "filling the ceiling");

        // Act
        Admit(limiter, "203.0.113.7", 10).Should().Be(0, "the ceiling is full");
        _time.Advance(TimeSpan.FromSeconds(31));

        // Assert
        Admit(limiter, "203.0.113.7", Window).Should().Be(Window - 1, "only the one admitted request was counted");
    }

    /// <summary>A spent window is whole again a minute after it started.</summary>
    [Fact]
    public void AWindowRestartsAfterItsMinute()
    {
        // Arrange
        using PrinterRouteLimiter limiter = new(_time);
        Admit(limiter, "203.0.113.7", Window + 1).Should().Be(Window);

        // Act
        _time.Advance(PrinterRateLimits.Window);

        // Assert
        Admit(limiter, "203.0.113.7", Window + 1).Should().Be(Window);
    }

    /// <summary>
    /// A full table forgets the caller seen least recently, not an arbitrary one - and forgetting only
    /// hands that caller a fresh window.
    /// </summary>
    [Fact]
    public void AFullTableForgetsTheCallerSeenLeastRecently()
    {
        // Arrange - two callers fit; A spends its window, B spends one permit.
        using PrinterRouteLimiter limiter = new(_time, capacity: 2);
        Admit(limiter, "192.0.2.1", Window).Should().Be(Window);
        Admit(limiter, "192.0.2.2", 1).Should().Be(1);

        // Act - a third caller evicts A, the least recently seen.
        Admit(limiter, "192.0.2.3", 1).Should().Be(1);

        // Assert
        Admit(limiter, "192.0.2.2", Window).Should().Be(Window - 1, "B was kept, with its count");
        Admit(limiter, "192.0.2.1", 1).Should().Be(1, "A was forgotten, and so starts afresh");
    }

    /// <summary>
    /// Being seen again - refused or not - makes a caller the most recent, so a caller still asking
    /// keeps its count while one that has gone quiet is forgotten. Otherwise a flood of new keys would
    /// wipe the window of the very caller it is refusing.
    /// </summary>
    [Fact]
    public void ACallerSeenAgainIsKeptOverOneThatWentQuiet()
    {
        // Arrange - A spends its window, then B arrives, then A asks again and is refused.
        using PrinterRouteLimiter limiter = new(_time, capacity: 2);
        Admit(limiter, "192.0.2.1", Window).Should().Be(Window);
        Admit(limiter, "192.0.2.2", 1).Should().Be(1);
        Admit(limiter, "192.0.2.1", 1).Should().Be(0);

        // Act - a third caller evicts whichever was seen least recently, which is now B.
        Admit(limiter, "192.0.2.3", 1).Should().Be(1);

        // Assert
        Admit(limiter, "192.0.2.1", 1).Should().Be(0, "A was seen more recently than B, so it kept its spent window");
    }

    /// <summary>
    /// An endpoint carrying no printer policy - every page - is admitted without counting, however many
    /// times it is asked.
    /// </summary>
    [Fact]
    public void AnEndpointWithNoPrinterPolicyIsNeverCounted()
    {
        using PrinterRouteLimiter limiter = new(_time);
        DefaultHttpContext page = new() { RequestServices = Services };

        for (int i = 0; i < Ceiling * 2; i += 1)
        {
            using RateLimitLease lease = limiter.AttemptAcquire(page);
            lease.IsAcquired.Should().BeTrue();
        }
    }

    private static IServiceProvider BuildServices()
    {
        ServiceCollection services = new();

        services.Configure<XForwardedOptions>(options =>
        {
            options.KnownProxies = ["172.28.0.2"];
            options.ClientAddressesUnreliable = false;
        });

        return services.BuildServiceProvider();
    }

    private static HttpContext Request(string address)
    {
        DefaultHttpContext context = new() { RequestServices = Services };

        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new EnableRateLimitingAttribute(Policy)), Policy));
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        return context;
    }

    /// <summary>How many of <paramref name="requests"/> from one address were admitted.</summary>
    private static int Admit(PrinterRouteLimiter limiter, string address, int requests)
    {
        int admitted = 0;

        for (int i = 0; i < requests; i += 1)
        {
            using RateLimitLease lease = limiter.AttemptAcquire(Request(address));
            admitted += lease.IsAcquired ? 1 : 0;
        }

        return admitted;
    }

    /// <summary>
    /// How many of <paramref name="callers"/> distinct addresses, one request each, were admitted.
    /// Numbered from <paramref name="first"/> within 10.0.0.0/8, so two calls can be kept apart.
    /// </summary>
    private static int AdmitOnceEach(PrinterRouteLimiter limiter, int callers, int first)
    {
        int admitted = 0;

        for (int i = first; i < first + callers; i += 1)
        {
            admitted += Admit(limiter, new IPAddress([10, (byte)(i >> 16), (byte)(i >> 8), (byte)i]).ToString(), 1);
        }

        return admitted;
    }
}
