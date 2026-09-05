using AwesomeAssertions;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Which claimed origins the passkey scheme accepts: a secure one under the relying-party id, and
/// plain http only on localhost.
/// </summary>
public sealed class PasskeyOriginTests
{
    [Theory]
    [InlineData("https://homespool.test", true)]
    [InlineData("https://app.homespool.test", true)]
    [InlineData("https://HOMESPOOL.test", true)]
    [InlineData("http://homespool.test", false)]
    [InlineData("https://evil.test", false)]
    [InlineData("https://nothomespool.test", false)]
    [InlineData("https://homespool.test.evil.test", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AnOriginIsAllowedOnlyUnderTheRelyingPartyIdOverTls(string? origin, bool allowed)
    {
        PasskeyAuthenticationOptions options = new() { ServerDomain = "homespool.test" };

        options.AllowsOrigin(origin).Should().Be(allowed);
    }

    [Theory]
    [InlineData("http://localhost", true)]
    [InlineData("http://localhost:5052", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://homespool.test", false)]
    public void PlainHttpIsAllowedOnLocalhostAlone(string origin, bool allowed)
    {
        PasskeyAuthenticationOptions localhost = new() { ServerDomain = "localhost" };
        PasskeyAuthenticationOptions named = new() { ServerDomain = "homespool.test" };

        (localhost.AllowsOrigin(origin) || named.AllowsOrigin(origin)).Should().Be(allowed);
    }

    [Fact]
    public void NothingIsAllowedWithoutARelyingPartyId()
    {
        new PasskeyAuthenticationOptions().AllowsOrigin("https://homespool.test").Should().BeFalse();
    }
}
