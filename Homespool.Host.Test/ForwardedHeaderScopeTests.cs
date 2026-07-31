using AwesomeAssertions;

using Homespool.Host.Listeners;

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
}
