using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Certificates;
using Homespool.Host.Middleware;

namespace Homespool.Host.Test;

/// <summary>
/// Forwarded headers are believed from the proxy by name, not from everything on its network - the
/// network also holds its bridge gateway, which is the Docker host.
/// </summary>
public sealed class ProxyHostAddressesTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("172.28.0.1");
    private static readonly IPAddress Proxy = IPAddress.Parse("172.28.0.3");
    private static readonly IPAddress MovedProxy = IPAddress.Parse("172.28.0.4");

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeResolver _resolver = new();

    private sealed class FakeResolver : IHostAddressResolver
    {
        public IPAddress[] Answer { get; set; } = [];

        public List<string> Asked { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            Asked.Add(name);
            Tokens.Add(cancellationToken);
            return Task.FromResult<IReadOnlyList<IPAddress>>(Answer);
        }
    }

    // ---- which peer is the proxy ----
    [Fact]
    public async Task TheAddressTheNameResolvesToIsTheProxyAndTheGatewayIsNot()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");

        (await addresses.IsProxyAsync(Proxy, CancellationToken.None)).Should().BeTrue();
        (await addresses.IsProxyAsync(Gateway, CancellationToken.None)).Should().BeFalse(
            "the gateway is inside the proxy's network, and it is the host, not the proxy");
    }

    [Fact]
    public async Task ANameThatDoesNotResolveTrustsNobody()
    {
        using ProxyHostAddresses addresses = NewAddresses("proxy");

        (await addresses.IsProxyAsync(Proxy, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AnIPv4PeerMappedIntoIPv6IsComparedAsIPv4()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");

        (await addresses.IsProxyAsync(Proxy.MapToIPv6(), CancellationToken.None)).Should().BeTrue();
    }

    /// <summary>
    /// A bare name is tried under the host's search domains once Docker's resolver stops knowing it,
    /// which it does while the proxy is down.
    /// </summary>
    [Theory]
    [InlineData("proxy", "proxy.")]
    [InlineData("proxy.", "proxy.")]
    [InlineData(" proxy ", "proxy.")]
    [InlineData("172.28.0.3", "172.28.0.3")]
    public async Task TheNameIsAskedForAsAbsolute(string configured, string asked)
    {
        using ProxyHostAddresses addresses = NewAddresses(configured);

        await addresses.IsProxyAsync(Proxy, CancellationToken.None);

        _resolver.Asked.Should().Equal(asked);
    }

    /// <summary>
    /// An aborted lookup answers empty and the answer is cached, so a lookup tied to a request would
    /// let any client that hung up leave the proxy untrusted.
    /// </summary>
    [Fact]
    public async Task TheLookupDoesNotCarryTheRequestsCancellation()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");
        using CancellationTokenSource request = new();

        await addresses.IsProxyAsync(Gateway, request.Token);

        _resolver.Tokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeFalse();
    }

    // ---- when it asks again ----
    [Fact]
    public async Task AnAnswerIsKeptForItsLifetime()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");

        await addresses.IsProxyAsync(Proxy, CancellationToken.None);
        _clock.Advance(ProxyHostAddresses.Lifetime - TimeSpan.FromMilliseconds(1));
        await addresses.IsProxyAsync(Proxy, CancellationToken.None);

        _resolver.Asked.Should().ContainSingle();
    }

    [Fact]
    public async Task AnExpiredAnswerIsAskedForAgain()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");
        await addresses.IsProxyAsync(Proxy, CancellationToken.None);

        _resolver.Answer = [];
        _clock.Advance(ProxyHostAddresses.Lifetime);

        (await addresses.IsProxyAsync(Proxy, CancellationToken.None)).Should().BeFalse();
        _resolver.Asked.Should().HaveCount(2);
    }

    /// <summary>
    /// The host connecting from the gateway over and over costs one lookup a second, not one a
    /// request.
    /// </summary>
    [Fact]
    public async Task AMissAsksAgainAtMostOnceASecond()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await addresses.IsProxyAsync(Gateway, CancellationToken.None);
        }

        _resolver.Asked.Should().ContainSingle();

        _clock.Advance(ProxyHostAddresses.RetryAfterMiss);
        await addresses.IsProxyAsync(Gateway, CancellationToken.None);

        _resolver.Asked.Should().HaveCount(2);
    }

    /// <summary>
    /// A proxy recreated on a new address is believed on its first request after the next lookup, not
    /// only once the old answer expires.
    /// </summary>
    [Fact]
    public async Task AProxyOnANewAddressIsBelievedWithoutWaitingForTheAnswerToExpire()
    {
        _resolver.Answer = [Proxy];
        using ProxyHostAddresses addresses = NewAddresses("proxy");
        await addresses.IsProxyAsync(Proxy, CancellationToken.None);

        _resolver.Answer = [MovedProxy];
        _clock.Advance(ProxyHostAddresses.RetryAfterMiss);

        (await addresses.IsProxyAsync(MovedProxy, CancellationToken.None)).Should().BeTrue();
    }

    /// <summary>
    /// The composition <c>Program</c> uses, over the real forwarded-headers middleware, reading what
    /// the rest of the application reads.
    /// </summary>
    [Theory]
    [InlineData("172.28.0.3", "proxy", "192.168.13.110", "https", "the proxy by name, inside the network")]
    [InlineData("172.28.0.1", "proxy", "172.28.0.1", "http", "the gateway is the host, inside the network but not the proxy")]
    [InlineData("172.28.0.1", "", "192.168.13.110", "https", "without the name the whole network is believed, gateway and all")]
    [InlineData("127.0.0.1", "proxy", "127.0.0.1", "http", "a name answering outside the network is not believed there")]
    public async Task ThePipelineBelievesTheProxyByNameWithinTheNetwork(string peer,
                                                                         string proxyHost,
                                                                         string expectedAddress,
                                                                         string expectedScheme,
                                                                         string because)
    {
        // Arrange
        _resolver.Answer = [Proxy, IPAddress.Loopback];
        XForwardedOptions forwarded = new() { KnownNetworks = ["172.28.0.0/16"], ProxyHost = proxyHost };

        ServiceCollection services = new();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<IOptions<XForwardedOptions>>(Options.Create(forwarded));
        services.AddSingleton<IHostAddressResolver>(_resolver);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<ProxyHostAddresses>();
        services.Configure<ForwardedHeadersOptions>(options => ForwardedHeadersConfigurator.Apply(forwarded, options));

        await using ServiceProvider provider = services.BuildServiceProvider();

        ApplicationBuilder app = new(provider);
        app.UseTrustedForwardedHeaders(forwarded);
        app.Run(_ => Task.CompletedTask);

        RequestDelegate pipeline = app.Build();

        DefaultHttpContext context = new() { RequestServices = provider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Real-IP"] = "192.168.13.110";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        await pipeline(context);

        // Assert
        context.Connection.RemoteIpAddress?.ToString().Should().Be(expectedAddress, because);
        context.Request.Scheme.Should().Be(expectedScheme, because);
    }

    private ProxyHostAddresses NewAddresses(string proxyHost)
    {
        return new ProxyHostAddresses(Options.Create(new XForwardedOptions { ProxyHost = proxyHost }),
                                      _resolver,
                                      _clock,
                                      NullLogger<ProxyHostAddresses>.Instance);
    }
}
