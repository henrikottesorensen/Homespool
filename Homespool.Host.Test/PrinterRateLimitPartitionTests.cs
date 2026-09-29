using System.Net;
using System.Threading.RateLimiting;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Middleware;
using Homespool.Host.PrusaConnect;
using Homespool.Host.RateLimiting;

namespace Homespool.Host.Test;

/// <summary>
/// Which window a printer request falls in beneath its route's ceiling.
/// </summary>
/// <remarks>
/// Driven through <see cref="PrinterRateLimits.Partition"/> rather than over HTTP for the reason the
/// sign-in tests give: the branch that matters most is the one that declines to partition by address,
/// and from the outside that looks like a window with room left in it. The partition key is the
/// observable - the address or the fingerprint for a window, empty for none.
/// </remarks>
public sealed class PrinterRateLimitPartitionTests
{
    private const string NoLimiter = "";

    private const string Address = "203.0.113.7";

    private static HttpContext Request(bool trustsProxy, bool? unreliable, string? fingerprint = null)
    {
        ServiceCollection services = new();

        services.Configure<XForwardedOptions>(options =>
        {
            options.KnownProxies = trustsProxy ? ["172.28.0.2"] : [];
            options.ClientAddressesUnreliable = unreliable;
        });

        DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };

        context.Connection.RemoteIpAddress = IPAddress.Parse(Address);

        if (fingerprint is not null)
        {
            context.Request.Headers[Headers.Fingerprint] = fingerprint;
        }

        return context;
    }

    /// <summary>
    /// Both registration verbs get a window per address where addresses are clients - the only
    /// partition a route naming no printer can have.
    /// </summary>
    [Theory]
    [InlineData(RateLimitPolicies.PrinterRegistrationStart)]
    [InlineData(RateLimitPolicies.PrinterRegistrationPoll)]
    public void RegistrationIsPartitionedByAddressWhereAddressesAreClients(string policy)
    {
        PrinterRateLimits.Partition(policy, Request(trustsProxy: true, unreliable: false)).PartitionKey.Should().Be(Address);
    }

    /// <summary>
    /// Where every client may share one address, a window per address would be one small window for
    /// everybody, so registration keeps its ceiling and nothing else: a host that says so, one that
    /// has not said, and one trusting no proxy.
    /// </summary>
    [Theory]
    [InlineData(RateLimitPolicies.PrinterRegistrationStart, true, true)]
    [InlineData(RateLimitPolicies.PrinterRegistrationStart, true, null)]
    [InlineData(RateLimitPolicies.PrinterRegistrationStart, false, false)]
    [InlineData(RateLimitPolicies.PrinterRegistrationPoll, true, true)]
    [InlineData(RateLimitPolicies.PrinterRegistrationPoll, true, null)]
    [InlineData(RateLimitPolicies.PrinterRegistrationPoll, false, false)]
    public void RegistrationIsNotPartitionedWhereAddressesMayBeShared(string policy, bool trustsProxy, bool? unreliable)
    {
        PrinterRateLimits.Partition(policy, Request(trustsProxy, unreliable)).PartitionKey.Should().Be(NoLimiter);
    }

    /// <summary>
    /// A route that names its printer stays partitioned by printer, whatever the address says - the
    /// fingerprint has no way to collapse into one window for the world.
    /// </summary>
    [Fact]
    public void AFingerprintedRouteIsPartitionedByPrinterEvenWhereAddressesAreClients()
    {
        HttpContext context = Request(trustsProxy: true, unreliable: false, fingerprint: "some-printer");

        PrinterRateLimits.Partition(RateLimitPolicies.PrinterSocket, context).PartitionKey.Should().NotBe(Address);
    }

    /// <summary>
    /// Each verb's window admits its own limit and refuses the next: five code requests - Buddy's
    /// three tries with room - and sixty-five polls, one SDK printer's second-by-second with room.
    /// </summary>
    [Theory]
    [InlineData(RateLimitPolicies.PrinterRegistrationStart, PrinterRateLimits.RegistrationStartPerAddressLimit)]
    [InlineData(RateLimitPolicies.PrinterRegistrationPoll, PrinterRateLimits.RegistrationPollPerAddressLimit)]
    public void AnAddressWindowAdmitsItsLimitAndRefusesTheNext(string policy, int limit)
    {
        // Arrange
        RateLimitPartition<string> partition = PrinterRateLimits.Partition(policy, Request(trustsProxy: true, unreliable: false));
        using RateLimiter limiter = partition.Factory(partition.PartitionKey);

        // Act
        int admitted = 0;
        for (int i = 0; i < limit; i += 1)
        {
            using RateLimitLease lease = limiter.AttemptAcquire();
            admitted += lease.IsAcquired ? 1 : 0;
        }

        using RateLimitLease next = limiter.AttemptAcquire();

        // Assert
        admitted.Should().Be(limit, "the window admits its permits");
        next.IsAcquired.Should().BeFalse("the permit after the last is refused");
    }
}
