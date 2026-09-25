using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A provider identity is its issuer and its subject together: the same subject from another issuer is
/// somebody else.
/// </summary>
/// <remarks>
/// The login provider is the scheme name, which does not change when <c>Oidc:Authority</c> is pointed
/// at a different provider, so the issuer has to be in the key or nothing tells the two providers'
/// user 1 apart.
/// </remarks>
public sealed class ProviderKeyTests : IDisposable
{
    private const string Provider = "oidc";

    private const string Issuer = "https://provider.example.net";

    private const string OtherIssuer = "https://other-provider.example.net";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-providerkey-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void TheKeyIsTheIssuerThenTheSubject()
    {
        ExternalSignIn.ProviderKey(Issuer, "1").Should().Be("https://provider.example.net 1");
        ExternalSignIn.ProviderKey(OtherIssuer, "1").Should().NotBe(ExternalSignIn.ProviderKey(Issuer, "1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("oidc")]
    [InlineData("LOCAL AUTHORITY")]
    [InlineData("https://provider.example.net/a b")]
    [InlineData("ftp://provider.example.net")]
    [InlineData("/relative")]
    public void AKeyIsNotMadeWithSomethingThatIsNotAnIssuer(string issuer)
    {
        Action made = () => ExternalSignIn.ProviderKey(issuer, "1");

        made.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AKeyIsNotMadeWithAnEmptySubject()
    {
        Action made = () => ExternalSignIn.ProviderKey(Issuer, string.Empty);

        made.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AnAnswerIsKeyedOnTheIssuerOfItsSubject()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        string answer = await AnswerAsync(rig, new Claim(JwtClaimTypes.Subject, "1", ClaimValueTypes.String, Issuer));

        ExternalLoginInfo? info = await ReadAsync(rig, answer);

        info!.ProviderKey.Should().Be(ExternalSignIn.ProviderKey(Issuer, "1"));
    }

    /// <summary>
    /// A subject no validated token vouched for carries the default issuer, or the scheme's name when
    /// the handler took it from userinfo; neither names a provider, so the answer is nobody's.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Provider)]
    [InlineData("https://provider.example.net/a b")]
    public async Task AnAnswerWhoseSubjectNamesNoIssuerIsRefused(string? issuer)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        string answer = await AnswerAsync(rig, new Claim(JwtClaimTypes.Subject, "1", ClaimValueTypes.String, issuer));

        (await ReadAsync(rig, answer)).Should().BeNull();
    }

    /// <summary>The finding itself: the same subject from another provider does not sign in to the account that holds it.</summary>
    [Fact]
    public async Task TheSameSubjectFromAnotherIssuerFindsNoAccount()
    {
        // Arrange - an account linked at the first provider, and the second provider's user with the same subject
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(owner, new UserLoginInfo(Provider, ExternalSignIn.ProviderKey(Issuer, "1"), "Dex"))).Succeeded.Should().BeTrue();
        string answer = await AnswerAsync(rig, new Claim(JwtClaimTypes.Subject, "1", ClaimValueTypes.String, OtherIssuer));

        // Act
        ExternalSignInResult result = await SignInAsync(rig, answer);

        // Assert
        result.Should().Be(ExternalSignInResult.NoAccount);
    }

    /// <summary>The counterweight: the first provider's own answer still signs in, or the refusal above proves nothing.</summary>
    [Fact]
    public async Task TheSameSubjectFromTheSameIssuerSignsIn()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(owner, new UserLoginInfo(Provider, ExternalSignIn.ProviderKey(Issuer, "1"), "Dex"))).Succeeded.Should().BeTrue();
        string answer = await AnswerAsync(rig, new Claim(JwtClaimTypes.Subject, "1", ClaimValueTypes.String, Issuer));

        ExternalSignInResult result = await SignInAsync(rig, answer);

        result.Should().Be(ExternalSignInResult.Succeeded);
    }

    /// <summary>The external cookie a provider's answer to a sign-in leaves, as the browser sends it back.</summary>
    private static async Task<string> AnswerAsync(LocalSchemeRig rig, Claim subject)
    {
        DefaultHttpContext request = rig.NewRequest();
        ClaimsPrincipal principal = new(new ClaimsIdentity([subject], Provider));

        await request.SignInAsync(IdentityConstants.ExternalScheme,
                                  principal,
                                  ExternalSignIn.ChallengeProperties(Provider, "/back", ExternalRoundTrip.SignIn));

        return rig.CookieOf(request, IdentityConstants.ExternalScheme);
    }

    private static async Task<ExternalLoginInfo?> ReadAsync(LocalSchemeRig rig, string answer)
    {
        DefaultHttpContext request = rig.NewRequest(answer);

        return await request.RequestServices.GetRequiredService<ExternalSignIn>().InfoAsync(request, ExternalRoundTrip.SignIn);
    }

    private static async Task<ExternalSignInResult> SignInAsync(LocalSchemeRig rig, string answer)
    {
        DefaultHttpContext request = rig.NewRequest(answer);
        ExternalSignIn external = request.RequestServices.GetRequiredService<ExternalSignIn>();
        ExternalLoginInfo info = (await external.InfoAsync(request, ExternalRoundTrip.SignIn))!;

        return await external.SignInAsync(request, info, isPersistent: false);
    }
}
