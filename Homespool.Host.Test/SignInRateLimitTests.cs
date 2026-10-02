using System.Net;
using System.Threading.RateLimiting;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Middleware;
using Homespool.Host.Pages.Account;

namespace Homespool.Host.Test;

/// <summary>
/// Which window a request to a sign-in page falls in.
/// </summary>
/// <remarks>
/// Driven through <see cref="SignInRateLimit.Partition"/> rather than over HTTP, because the branch
/// that matters is the one that declines to limit at all - and from the outside that is
/// indistinguishable from a window with room left in it. The partition key is the observable: the
/// address for a window, empty for no limiter.
/// </remarks>
public sealed class SignInRateLimitTests
{
    private const string NoLimiter = "";

    private static HttpContext Request(string method,
                                       bool trustsProxy,
                                       string? address = "203.0.113.7",
                                       string? handler = null,
                                       bool? unreliable = false)
    {
        ServiceCollection services = new();

        services.Configure<XForwardedOptions>(options =>
        {
            options.KnownProxies = trustsProxy ? ["172.28.0.2"] : [];
            options.ClientAddressesUnreliable = unreliable;
        });

        DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };

        context.Request.Method = method;
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);

        if (handler is not null)
        {
            context.Request.QueryString = new QueryString($"?handler={handler}");
        }

        return context;
    }

    /// <summary>A page render is never refused, however spent the window is.</summary>
    [Fact]
    public void AGetIsNotLimited()
    {
        SignInRateLimit.Partition(Request(HttpMethods.Get, trustsProxy: true)).PartitionKey.Should().Be(NoLimiter);
    }

    /// <summary>
    /// With a proxy named on a host that keeps each client's address, the address is the client's and
    /// gets its own window.
    /// </summary>
    [Fact]
    public void APostIsLimitedPerAddressWhenAProxyIsTrusted()
    {
        RateLimitPartition<string> partition = SignInRateLimit.Partition(Request(HttpMethods.Post, trustsProxy: true));

        partition.PartitionKey.Should().Be("203.0.113.7");
    }

    /// <summary>
    /// With no proxy named, every visitor would arrive as the proxy's own address, so one flood would
    /// close the sign-in form to everybody. Refusing to limit is the safer failure.
    /// </summary>
    [Fact]
    public void APostIsNotLimitedWhenNoProxyIsTrusted()
    {
        SignInRateLimit.Partition(Request(HttpMethods.Post, trustsProxy: false)).PartitionKey.Should().Be(NoLimiter);
    }

    /// <summary>
    /// A proxy trusted on a host that gives every client the same address - Docker Desktop's port
    /// forwarder, measured - is still one window for the world, so the form is left unlimited.
    /// </summary>
    [Fact]
    public void APostIsNotLimitedWhenTheHostGivesEveryClientOneAddress()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: true, unreliable: true);

        SignInRateLimit.Partition(context).PartitionKey.Should().Be(NoLimiter);
    }

    /// <summary>
    /// A deployment that has never said is treated like one that said its addresses are unreliable:
    /// what every deployment had before the question was asked.
    /// </summary>
    [Fact]
    public void APostIsNotLimitedWhenNobodyHasSaidWhetherAddressesSurvive()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: true, unreliable: null);

        SignInRateLimit.Partition(context).PartitionKey.Should().Be(NoLimiter);
    }

    /// <summary>
    /// The passkey challenge keeps the rule it had before this policy took over its page: limited
    /// whether or not a proxy is trusted, because it has the password form beside it as a fallback.
    /// </summary>
    [Fact]
    public void ThePasskeyChallengeIsLimitedEvenWithNoProxyTrusted()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: false, handler: LoginModel.PasskeyOptionsHandler);

        SignInRateLimit.Partition(context).PartitionKey.Should().Be("203.0.113.7");
    }

    /// <summary>
    /// A repeated handler parameter runs the handler its first value names, so a challenge named first
    /// keeps the challenge's window with no proxy trusted, rather than falling to the credential branch,
    /// which declines to limit.
    /// </summary>
    [Fact]
    public void AChallengeNamedFirstOfTwoHandlerValuesIsStillLimitedWithNoProxyTrusted()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: false);
        context.Request.QueryString = new QueryString($"?handler={LoginModel.PasskeyOptionsHandler}&handler=Other");

        SignInRateLimit.Partition(context).PartitionKey.Should().Be("203.0.113.7");
    }

    /// <summary>
    /// An IPv6 client is its /64: every address a host can pick inside it is one window, so rotating
    /// addresses buys no more attempts.
    /// </summary>
    [Fact]
    public void AnIPv6PostIsLimitedPerSlash64()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: true, address: "2001:db8:1:2::abcd");

        SignInRateLimit.Partition(context).PartitionKey.Should().Be("2001:db8:1:2::/64");
    }

    /// <summary>The login page's challenge is keyed the same way as the credential handlers.</summary>
    [Fact]
    public void AnIPv6ChallengeIsLimitedPerSlash64()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: false, address: "2001:db8:1:2::abcd",
                                      handler: LoginModel.PasskeyOptionsHandler);

        SignInRateLimit.Partition(context).PartitionKey.Should().Be("2001:db8:1:2::/64");
    }

    /// <summary>
    /// An address the connection cannot name still partitions rather than throwing - one shared
    /// window, which is what a test host and a unix socket both look like.
    /// </summary>
    [Fact]
    public void AnAddresslessConnectionSharesOneWindow()
    {
        HttpContext context = Request(HttpMethods.Post, trustsProxy: true, address: null);

        SignInRateLimit.Partition(context).PartitionKey.Should().Be("unknown");
    }
}
