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
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The stamp validators, driven through the cookie schemes they are wired to: a session or a
/// remembered browser older than the validation interval is re-checked against the account's
/// security stamp, kept when it matches and ended when it does not.
/// </summary>
/// <remarks>
/// <b>One clock for the cookies and the validators.</b> The container's <see cref="TimeProvider"/>
/// reaches both, so a cookie is issued at the fake now and the validators measure from the same
/// clock; advancing it past the interval before the next request is what makes a cookie "aged".
/// </remarks>
public sealed class StampValidatorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-stamp-{Guid.NewGuid():N}.db");
    private static readonly TimeSpan PastTheInterval = TimeSpan.FromMinutes(31);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

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

    private Task<LocalSchemeRig> RigAsync()
    {
        return LocalSchemeRig.CreateAsync(_databasePath, services => services.AddSingleton<TimeProvider>(_clock));
    }

    [Fact]
    public async Task AnAgedSessionWithTheSameStampIsRebuiltKeepingItsMethod()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        DefaultHttpContext signIn = rig.NewRequest();
        ClaimsPrincipal principal = await rig.PrincipalOf(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, PasskeyAuthenticationHandler.AuthenticationMethod));
        await LocalSchemeRig.SignInOf(signIn).SignInAsync(signIn, principal, isPersistent: false);
        string cookie = rig.CookieOf(signIn, IdentityConstants.ApplicationScheme);

        // A claim added to the account: something the factory reads and that, unlike a rename or a new
        // address, leaves the security stamp where it is.
        (await rig.Users.AddClaimAsync(user, new Claim(JwtClaimTypes.NickName, "the owner"))).Succeeded.Should().BeTrue();
        _clock.Advance(PastTheInterval);

        DefaultHttpContext later = rig.NewRequest(cookie);
        AuthenticateResult session = await later.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        session.Succeeded.Should().BeTrue("the stamp still matches");
        ClaimsPrincipal rebuilt = session.Principal!;
        rebuilt.FindFirstValue(JwtClaimTypes.NickName).Should().Be("the owner", "the principal is rebuilt from the account");
        rebuilt.FindFirstValue(JwtClaimTypes.AuthenticationMethod).Should().Be(PasskeyAuthenticationHandler.AuthenticationMethod, "how the person signed in survives the rebuild");
    }

    [Fact]
    public async Task AnAgedSessionWithAChangedStampIsEndedAlongWithEverythingElse()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string session = await rig.SessionCookieAsync(user);
        string remembered = await rig.RememberedMachineCookieAsync(user);

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
        _clock.Advance(PastTheInterval);

        DefaultHttpContext later = rig.NewRequest(session, remembered);
        AuthenticateResult result = await later.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        result.Succeeded.Should().BeFalse("the account changed underneath this browser");
        rig.Cleared(later, IdentityConstants.ApplicationScheme).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeTrue("a stale stamp forgets the browser too");
    }

    /// <summary>
    /// The remembered cookie carries the stamp, so an aged one is still honoured while the account is
    /// unchanged. Without the stamp claim the check would fail on the first aged read and, as the
    /// framework's validator does, end the session with it.
    /// </summary>
    [Fact]
    public async Task AnAgedRememberedBrowserWithTheSameStampIsStillRemembered()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string remembered = await rig.RememberedMachineCookieAsync(user);
        _clock.Advance(PastTheInterval);

        DefaultHttpContext later = rig.NewRequest(remembered);

        (await LocalSchemeRig.RulesOf(later).IsTwoFactorClientRememberedAsync(later, user)).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeFalse();
    }

    [Fact]
    public async Task AnAgedRememberedBrowserWithAChangedStampIsForgottenAndTheSessionEnded()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string remembered = await rig.RememberedMachineCookieAsync(user);

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
        _clock.Advance(PastTheInterval);

        DefaultHttpContext later = rig.NewRequest(remembered);

        (await LocalSchemeRig.RulesOf(later).IsTwoFactorClientRememberedAsync(later, user)).Should().BeFalse();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.ApplicationScheme).Should().BeTrue("the framework ends the session on a stale remembered browser, and so does this");
    }
}
