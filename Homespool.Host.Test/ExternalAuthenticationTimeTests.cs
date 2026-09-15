using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;

using AwesomeAssertions;

using Duende.IdentityModel;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// A provider's answer arrives with <c>auth_time</c> set to this server's clock, and with whatever time
/// the provider reported moved to <see cref="HSClaimTypes.ExternalAuthenticationTime"/>.
/// </summary>
public sealed class ExternalAuthenticationTimeTests
{
    private const string ProviderIssuer = "https://provider.example.net";

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnAnswerWithoutAProviderTimeGetsThisServersAndNothingElse()
    {
        // Arrange
        ClaimsPrincipal answer = Answer();

        // Act
        ExternalSignIn.RestateAuthenticationTime(answer, Now);

        // Assert
        Claim authTime = answer.Claims.Should().ContainSingle(claim => claim.Type == JwtClaimTypes.AuthenticationTime).Subject;
        authTime.Value.Should().Be(Seconds(Now));
        authTime.Issuer.Should().Be(ClaimsIdentity.DefaultIssuer, "the time is this server's, not the provider's");
        answer.FindFirst(HSClaimTypes.ExternalAuthenticationTime).Should().BeNull("the provider reported nothing to move aside");
    }

    [Fact]
    public void TheProvidersTimeIsMovedAsideWithItsIssuer()
    {
        // Arrange
        DateTimeOffset signedInThere = Now.AddHours(-3);
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.AuthenticationTime, Seconds(signedInThere), ClaimValueTypes.Integer64, ProviderIssuer));

        // Act
        ExternalSignIn.RestateAuthenticationTime(answer, Now);

        // Assert
        answer.Claims.Should().ContainSingle(claim => claim.Type == JwtClaimTypes.AuthenticationTime)
              .Which.Value.Should().Be(Seconds(Now), "the provider's value does not survive under the server's name");

        Claim moved = answer.Claims.Should().ContainSingle(claim => claim.Type == HSClaimTypes.ExternalAuthenticationTime).Subject;
        moved.Value.Should().Be(Seconds(signedInThere));
        moved.Issuer.Should().Be(ProviderIssuer, "a reader can still tell whose clock this is");
    }

    [Fact]
    public void AProviderCannotSupplyEitherTimeUnderTheOtherName()
    {
        // Arrange - a provider sending its own external_auth_time, beside no auth_time at all
        ClaimsPrincipal answer = Answer(new Claim(HSClaimTypes.ExternalAuthenticationTime, Seconds(Now.AddMinutes(1)), ClaimValueTypes.Integer64, ProviderIssuer));

        // Act
        ExternalSignIn.RestateAuthenticationTime(answer, Now);

        // Assert
        answer.FindFirst(HSClaimTypes.ExternalAuthenticationTime)
              .Should().BeNull("only a provider's auth_time becomes an external_auth_time, so a fresh one cannot be sent to cover a stale one");
        answer.Claims.Where(claim => claim.Type == JwtClaimTypes.AuthenticationTime).Select(claim => claim.Value)
              .Should().Equal(Seconds(Now));
    }

    private static ClaimsPrincipal Answer(params Claim[] times)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtClaimTypes.Subject, "subject-1", ClaimValueTypes.String, ProviderIssuer), .. times], "oidc"));
    }

    private static string Seconds(DateTimeOffset time)
    {
        return time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }
}
