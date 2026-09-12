using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The cookies a sign-in writes and clears: each <see cref="LocalSignIn"/> method touches exactly the
/// cookies its name says, and what one request wrote the next request reads back through
/// <see cref="LocalSignInRules"/>.
/// </summary>
public sealed class LocalSignInTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-signin-{Guid.NewGuid():N}.db");

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
    public async Task SigningInWritesTheSessionAndClearsWhatItSupersedes()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext request = rig.NewRequest();

        await LocalSchemeRig.SignInOf(request).SignInAsync(request, await rig.PrincipalOf(user), isPersistent: false);

        rig.CookieOf(request, IdentityConstants.ApplicationScheme).Should().NotBeEmpty();
        rig.Cleared(request, IdentityConstants.TwoFactorUserIdScheme).Should().BeTrue("a completed sign-in owes no second factor any more");
        rig.Cleared(request, IdentityConstants.ExternalScheme).Should().BeTrue("the provider's ticket has been spent");
        rig.Cleared(request, IdentityConstants.TwoFactorRememberMeScheme).Should().BeFalse("a remembered browser stays remembered");
        request.User.FindFirstValue(JwtClaimTypes.Subject).Should().Be(user.Id.ToString(), "the rest of the request sees the person as signed in");
    }

    [Fact]
    public async Task TheSessionCarriesTheSchemesPrincipalAndTheProviderItWasPendingFor()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext signIn = rig.NewRequest();
        ClaimsPrincipal principal = await rig.PrincipalOf(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, TotpAuthenticationHandler.AuthenticationMethod));

        await LocalSchemeRig.SignInOf(signIn).SignInAsync(signIn, principal, isPersistent: true, loginProvider: "dex");

        DefaultHttpContext next = rig.NewRequest(rig.CookieOf(signIn, IdentityConstants.ApplicationScheme));
        AuthenticateResult session = await next.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        session.Succeeded.Should().BeTrue();
        ClaimsPrincipal signedIn = session.Principal!;
        signedIn.FindFirstValue(JwtClaimTypes.AuthenticationMethod).Should().Be(TotpAuthenticationHandler.AuthenticationMethod, "the scheme's own tag survives onto the session");
        signedIn.FindFirstValue(JwtClaimTypes.IdentityProvider).Should().Be("dex");
        session.Properties!.IsPersistent.Should().BeTrue();
    }

    [Fact]
    public async Task APendingSecondFactorIsReadBackWithItsProvider()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");

        DefaultHttpContext next = rig.NewRequest(await rig.PendingTwoFactorCookieAsync(user, "dex"));
        LocalSignInRules rules = LocalSchemeRig.RulesOf(next);

        (await rules.PendingTwoFactorAccountAsync(next))!.Id.Should().Be(user.Id);
        (await rules.PendingLoginProviderAsync(next)).Should().Be("dex");
        (await rules.SignedInAccountAsync(next)).Should().BeNull("a pending account is not signed in");
    }

    [Fact]
    public async Task ASecondFactorIsOwedOnlyWhenEnabledAndNotRemembered()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext before = rig.NewRequest();

        bool withoutAuthenticator = await LocalSchemeRig.SignInOf(before).OwesSecondFactorAsync(before, user);

        await rig.EnableAuthenticatorAsync(user);
        DefaultHttpContext enabled = rig.NewRequest();
        bool withAuthenticator = await LocalSchemeRig.SignInOf(enabled).OwesSecondFactorAsync(enabled, user);

        DefaultHttpContext remembered = rig.NewRequest(await rig.RememberedMachineCookieAsync(user));
        bool onARememberedBrowser = await LocalSchemeRig.SignInOf(remembered).OwesSecondFactorAsync(remembered, user);

        withoutAuthenticator.Should().BeFalse();
        withAuthenticator.Should().BeTrue();
        onARememberedBrowser.Should().BeFalse();
    }

    [Fact]
    public async Task ARememberedBrowserIsRememberedForThatAccountAlone()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        string remembered = await rig.RememberedMachineCookieAsync(owner);

        DefaultHttpContext next = rig.NewRequest(remembered);
        LocalSignInRules rules = LocalSchemeRig.RulesOf(next);

        (await rules.IsTwoFactorClientRememberedAsync(next, owner)).Should().BeTrue();
        (await rules.IsTwoFactorClientRememberedAsync(next, other)).Should().BeFalse();
    }

    [Fact]
    public async Task ForgettingTheBrowserClearsTheRememberedCookie()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext request = rig.NewRequest(await rig.RememberedMachineCookieAsync(user));

        await LocalSchemeRig.SignInOf(request).ForgetClientAsync(request);

        rig.Cleared(request, IdentityConstants.TwoFactorRememberMeScheme).Should().BeTrue();
    }

    [Fact]
    public async Task SigningOutClearsTheSessionAndAnythingInProgress()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user), await rig.RememberedMachineCookieAsync(user));

        await LocalSchemeRig.SignInOf(request).SignOutAsync(request);

        rig.Cleared(request, IdentityConstants.ApplicationScheme).Should().BeTrue();
        rig.Cleared(request, IdentityConstants.ExternalScheme).Should().BeTrue();
        rig.Cleared(request, IdentityConstants.TwoFactorUserIdScheme).Should().BeTrue();
        rig.Cleared(request, IdentityConstants.TwoFactorRememberMeScheme).Should().BeFalse("signing out does not forget the browser, as the framework leaves it");
    }

    /// <summary>The proof goes with the session, whichever route out of it is taken.</summary>
    [Fact]
    public async Task SigningOutClearsTheRecentProof()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user));

        await LocalSchemeRig.SignInOf(request).SignOutAsync(request);

        request.Response.Headers.SetCookie.ToString().Should().Contain("Homespool.RecentProof=;");
    }
}
