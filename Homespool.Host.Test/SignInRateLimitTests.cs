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

    private static HttpContext Request(string method, bool trustsProxy, string? address = "203.0.113.7", string? handler = null)
    {
        ServiceCollection services = new();

        services.Configure<XForwardedOptions>(options => options.KnownProxies = trustsProxy ? ["172.28.0.2"] : []);

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

    /// <summary>With a proxy named, the address is the client's and gets its own window.</summary>
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
