using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Which claimed origins the passkey scheme accepts: a secure one on a name this deployment serves
/// people on, under the relying-party id, on the port the request arrived on - and plain http only on
/// localhost.
/// </summary>
public sealed class PasskeyOriginTests
{
    private const string RelyingPartyId = "homespool.test";

    /// <summary>
    /// With no served names configured, the relying-party id is the one name accepted - not its
    /// subdomains, though a browser would let a page on one run a ceremony against it.
    /// </summary>
    [Theory]
    [InlineData("https://homespool.test", true)]
    [InlineData("https://HOMESPOOL.test", true)]
    [InlineData("https://app.homespool.test", false)]
    [InlineData("http://homespool.test", false)]
    [InlineData("https://evil.test", false)]
    [InlineData("https://nothomespool.test", false)]
    [InlineData("https://homespool.test.evil.test", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WithNoServedNamesOnlyTheRelyingPartyIdIsAllowedOverTls(string? origin, bool allowed)
    {
        PasskeyAuthenticationOptions options = new() { ServerDomain = RelyingPartyId };

        options.AllowsOrigin(origin, new HostString(RelyingPartyId)).Should().Be(allowed);
    }

    [Theory]
    [InlineData("https://homespool.test")]
    [InlineData("https://app.homespool.test")]
    [InlineData("https://APP.Homespool.test")]
    public void AnExactServedNamePasses(string origin)
    {
        PasskeyAuthenticationOptions options = Serving("homespool.test", "app.homespool.test");

        options.AllowsOrigin(origin, new HostString("app.homespool.test")).Should().BeTrue();
    }

    /// <summary>
    /// A page on a subdomain nobody listed can still run a ceremony against the relying-party id, so
    /// the name in the client data is refused unless it is served - and a served name the id does not
    /// cover is refused too, since no credential of this deployment could answer from it.
    /// </summary>
    [Theory]
    [InlineData("https://evil.homespool.test")]
    [InlineData("https://deeper.app.homespool.test")]
    [InlineData("https://other.test")]
    public void ANameThatIsNotBothServedAndCoveredIsRefused(string origin)
    {
        PasskeyAuthenticationOptions options = Serving("homespool.test", "app.homespool.test", "other.test");

        options.AllowsOrigin(origin, new HostString("homespool.test")).Should().BeFalse();
    }

    /// <summary>
    /// The port is the request's: the one its <c>Host</c> names, or the scheme's default when it
    /// names none.
    /// </summary>
    [Theory]
    [InlineData("https://homespool.test", "homespool.test", true)]
    [InlineData("https://homespool.test", "homespool.test:443", true)]
    [InlineData("https://homespool.test:8443", "homespool.test:8443", true)]
    [InlineData("https://homespool.test:8443", "homespool.test", false)]
    [InlineData("https://homespool.test", "homespool.test:8443", false)]
    [InlineData("https://homespool.test:9443", "homespool.test:8443", false)]
    public void AnotherPortIsRefused(string origin, string requestHost, bool allowed)
    {
        PasskeyAuthenticationOptions options = Serving("homespool.test");

        options.AllowsOrigin(origin, new HostString(requestHost)).Should().Be(allowed);
    }

    /// <summary>
    /// The served names come from <c>AllowedHosts</c> as the host filter reads it. Unset, empty or
    /// holding <c>*</c> falls back to the relying-party id alone, never to its subdomains; and a
    /// <c>*.</c> pattern matches nothing, because only a whole name is.
    /// </summary>
    [Theory]
    [InlineData(null, "https://homespool.test", true)]
    [InlineData(null, "https://app.homespool.test", false)]
    [InlineData("", "https://app.homespool.test", false)]
    [InlineData("*", "https://homespool.test", true)]
    [InlineData("*", "https://app.homespool.test", false)]
    [InlineData("localhost;*", "https://homespool.test", true)]
    [InlineData("localhost;*", "https://app.homespool.test", false)]
    [InlineData("homespool.test; app.homespool.test ;localhost", "https://app.homespool.test", true)]
    [InlineData("app.homespool.test;localhost", "https://homespool.test", false)]
    [InlineData("*.homespool.test", "https://app.homespool.test", false)]
    public void TheServedNamesAreReadFromAllowedHosts(string? allowedHosts, string origin, bool allowed)
    {
        PasskeyAuthenticationOptions options = Configured(allowedHosts);

        options.AllowsOrigin(origin, new HostString(new Uri(origin).Host)).Should().Be(allowed);
    }

    [Theory]
    [InlineData("http://localhost", "localhost", true)]
    [InlineData("http://localhost:5052", "localhost:5052", true)]
    [InlineData("https://localhost:5001", "localhost:5001", true)]
    [InlineData("http://localhost:5052", "localhost:5053", false)]
    public void PlainHttpIsAllowedOnLocalhost(string origin, string requestHost, bool allowed)
    {
        PasskeyAuthenticationOptions options = new() { ServerDomain = "localhost" };

        options.AllowsOrigin(origin, new HostString(requestHost)).Should().Be(allowed);
    }

    [Fact]
    public void PlainHttpIsRefusedOffLocalhostEvenWhenServed()
    {
        PasskeyAuthenticationOptions options = Serving("homespool.test", "localhost");

        options.AllowsOrigin("http://homespool.test", new HostString("homespool.test")).Should().BeFalse();
    }

    [Fact]
    public void NothingIsAllowedWithoutARelyingPartyId()
    {
        new PasskeyAuthenticationOptions().AllowsOrigin("https://homespool.test", new HostString("homespool.test")).Should().BeFalse();
    }

    [Fact]
    public void NothingIsAllowedWithoutARequestHost()
    {
        Serving("homespool.test").AllowsOrigin("https://homespool.test", default).Should().BeFalse();
    }

    private static PasskeyAuthenticationOptions Serving(params string[] names)
    {
        return new PasskeyAuthenticationOptions { ServerDomain = RelyingPartyId, ServedHosts = names };
    }

    /// <summary>The scheme's options as the application builds them, over an <c>AllowedHosts</c> value.</summary>
    private static PasskeyAuthenticationOptions Configured(string? allowedHosts)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                                              .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts })
                                              .Build());
        services.Configure<Middleware.SecurityOptions>(security => security.PasskeyServerDomain = RelyingPartyId);
        services.AddAuthentication().AddPasskeyAuthentication();

        using ServiceProvider provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptionsMonitor<PasskeyAuthenticationOptions>>().Get(Schemes.Passkey);
    }
}
