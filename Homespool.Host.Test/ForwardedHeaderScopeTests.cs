using System;
using System.Net;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Listeners;
using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// Where a forwarded header may be believed, which is the whole of what stops a client choosing its
/// own apparent address.
/// </summary>
/// <remarks>
/// The rule moved when nginx took over printer TLS, and it moved in the direction that trusts more
/// rather than less — so it is worth asserting in both configurations rather than only in the one
/// that ships. <c>ListenerSegregationTests</c> is the companion for the routing half of the same
/// boundary.
/// </remarks>
public class ForwardedHeaderScopeTests
{
    private const int PrinterPort = 15443;
    private const int UserPort = 8080;

    /// <summary>
    /// The user listener, always. Its port is not published, so the proxy is the only client it can
    /// have, in either configuration.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheUserListenerIsAlwaysTrusted(bool printerListenerIsProxied)
    {
        ForwardedHeaderScope.AppliesTo(UserPort, PrinterPort, printerListenerIsProxied)
                            .Should().BeTrue();
    }

    /// <summary>
    /// The printer listener with nginx in front, which is the shipped stack: the port is unpublished
    /// and nothing but the proxy can reach it, so <c>X-Real-IP</c> there is the proxy's word.
    /// </summary>
    [Fact]
    public void ThePrinterListenerIsTrustedWhenTheProxyTerminatesItsTls()
    {
        ForwardedHeaderScope.AppliesTo(PrinterPort, PrinterPort, printerListenerIsProxied: true)
                            .Should().BeTrue();
    }

    /// <summary>
    /// <b>The case this exists to refuse.</b> With <c>PrusaConnect:PrinterTls</c> off, printers dial
    /// that port directly and the header is written by whoever connected — so honouring it would let
    /// anything holding a printer token claim any address it liked, in the logs and in anything else
    /// keyed on address.
    /// </summary>
    [Fact]
    public void ThePrinterListenerIsNotTrustedWhenPrintersDialItDirectly()
    {
        ForwardedHeaderScope.AppliesTo(PrinterPort, PrinterPort, printerListenerIsProxied: false)
                            .Should().BeFalse();
    }

    /// <summary>
    /// A port that is neither is treated as the user listener rather than the printer one, which is
    /// the safe direction here and the opposite of the one
    /// <see cref="ListenerSegregationMiddleware"/> takes. The two are not inconsistent: an
    /// unidentifiable port must never serve printer <i>routes</i>, and it also cannot be the
    /// unpublished printer listener — so there is nothing about it that argues for refusing the
    /// header the proxy sends.
    /// </summary>
    [Fact]
    public void AnyOtherPortIsTreatedAsProxied()
    {
        ForwardedHeaderScope.AppliesTo(9999, PrinterPort, printerListenerIsProxied: false)
                            .Should().BeTrue();
    }

    /// <summary>
    /// <b>The decision is taken from the port the connection ARRIVED on, not the one it came from.</b>
    /// </summary>
    /// <remarks>
    /// The tests above all pass an <c>int</c>, so every one of them stays green whether the caller
    /// reads <c>LocalPort</c> or <c>RemotePort</c> — and reading the remote port would let a client
    /// pick its own answer by choosing a source port, handing away the whole protection. This is the
    /// case that tells the two apart, which is why the predicate takes an <c>HttpContext</c> rather
    /// than being written inline at the call site.
    /// </remarks>
    [Fact]
    public void ThePredicateReadsTheLocalPortRatherThanTheRemoteOne()
    {
        // Arrange - arrived on the printer listener, which is not proxied, so it must be refused.
        // The remote port is the user port, so anything reading that instead would allow it.
        DefaultHttpContext context = new();
        context.Connection.LocalPort = PrinterPort;
        context.Connection.RemotePort = UserPort;

        // Act
        bool applies = ForwardedHeaderScope.Predicate(PrinterPort, printerListenerIsProxied: false)(context);

        // Assert
        applies.Should().BeFalse(
            "the port a connection arrived on is a property of the socket, and the port it came from is "
            + "the client's to choose");
    }

    /// <summary>
    /// The predicate and <c>UseForwardedHeaders</c> together actually decide whether the client's
    /// stated address is believed.
    /// </summary>
    /// <remarks>
    /// Asserting the rule is not the same as asserting the behaviour: a correct predicate wired into
    /// the wrong branch, or onto a middleware that never runs, would satisfy every other test in this
    /// file. This runs the composition and looks at <c>RemoteIpAddress</c>, which is what the rest of
    /// the application reads and what ends up in the log.
    /// </remarks>
    [Theory]
    [InlineData(UserPort, false, "192.168.13.110", "the user listener is only reachable through the proxy")]
    [InlineData(PrinterPort, true, "192.168.13.110", "nginx terminates printer TLS, so X-Real-IP is its word")]
    [InlineData(PrinterPort, false, "10.9.9.9", "printers dial this port directly, so the header is the caller's own")]
    public async Task TheBranchDecidesWhetherTheStatedAddressIsBelieved(
        int arrivedOnPort, bool printerListenerIsProxied, string expectedAddress, string because)
    {
        // Arrange - the pipeline as Program.cs composes it, over the real forwarded-headers middleware.
        ServiceCollection services = new();
        services.AddOptions();
        services.AddLogging();
        services.Configure<ForwardedHeadersOptions>(options =>
            ForwardedHeadersConfigurator.Apply(
                new XForwardedOptions { KnownProxies = ["10.9.9.9"] }, options));

        using ServiceProvider provider = services.BuildServiceProvider();

        ApplicationBuilder app = new(provider);
        app.UseWhen(
            ForwardedHeaderScope.Predicate(PrinterPort, printerListenerIsProxied),
            branch => branch.UseForwardedHeaders());
        app.Run(_ => Task.CompletedTask);

        RequestDelegate pipeline = app.Build();

        // 10.9.9.9 is the trusted peer - nginx, in the shipped stack - claiming to speak for a printer.
        DefaultHttpContext context = new();
        context.Connection.LocalPort = arrivedOnPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.9.9.9");
        context.Request.Headers["X-Real-IP"] = "192.168.13.110";

        // Act
        await pipeline(context);

        // Assert
        context.Connection.RemoteIpAddress?.ToString().Should().Be(expectedAddress, because);
    }
}
