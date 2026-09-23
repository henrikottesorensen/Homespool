using System;
using System.Buffers.Text;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The two validators, driven through the cookie schemes they are wired to: a session is checked
/// against its row on every request and nothing more, and a remembered browser is re-checked against
/// the account's security stamp every time it is read.
/// </summary>
/// <remarks>
/// <b>One clock for the cookies and the validators.</b> The container's <see cref="TimeProvider"/>
/// reaches both, so a cookie is issued at the fake now; advancing it is what makes a cookie "aged",
/// which is what the framework's thirty-minute interval would have turned on.
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

    /// <summary>
    /// Nothing re-reads the account into the cookie on a timer: a claim added underneath an aged
    /// session leaves it signed in, unchanged and not re-issued. A person's own changes reach their
    /// cookie through the refresh; a change to somebody else's account moves its stamp.
    /// </summary>
    [Fact]
    public async Task AnAgedSessionIsNeitherRebuiltNorReissued()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(rig, user);
        string cookie = await PasskeySessionCookieAsync(rig, user, Base64Url.EncodeToString(phone.CredentialId));

        // A claim added to the account: something the factory reads and that, unlike a rename or a new
        // address, leaves the security stamp where it is.
        (await rig.Users.AddClaimAsync(user, new Claim(JwtClaimTypes.NickName, "the owner"))).Succeeded.Should().BeTrue();
        _clock.Advance(PastTheInterval);

        DefaultHttpContext later = rig.NewRequest(cookie);
        AuthenticateResult session = await later.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        session.Succeeded.Should().BeTrue("the stamp still matches");
        session.Principal!.FindFirstValue(JwtClaimTypes.NickName).Should().BeNull("the principal is the cookie's, not rebuilt from the account");
        session.Principal!.FindFirstValue(HSClaimTypes.PasskeyCredentialId).Should().Be(Base64Url.EncodeToString(phone.CredentialId));
        later.Response.Headers.SetCookie.Should().BeEmpty("an aged session inside its sliding window is not re-issued");
    }

    /// <summary>
    /// The lost-device case: the passkey is removed from another browser, and only the sessions that
    /// signed in with it end - not the owner's session on a second passkey, and not their password
    /// session, which is the one they are most likely revoking from. On the very next request: no
    /// clock moves here.
    /// </summary>
    [Fact]
    public async Task ASessionWhosePasskeyWasRemovedIsEndedAtOnceAndNoOtherSessionIs()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(rig, user);
        UserPasskeyInfo laptop = await SeedPasskeyAsync(rig, user);
        string phoneSession = await PasskeySessionCookieAsync(rig, user, Base64Url.EncodeToString(phone.CredentialId));
        string laptopSession = await PasskeySessionCookieAsync(rig, user, Base64Url.EncodeToString(laptop.CredentialId));
        string passwordSession = await rig.SessionCookieAsync(user);

        await rig.Users.RemovePasskeyAsync(user, phone.CredentialId);

        DefaultHttpContext onPhone = rig.NewRequest(phoneSession);
        DefaultHttpContext onLaptop = rig.NewRequest(laptopSession);
        DefaultHttpContext withPassword = rig.NewRequest(passwordSession);

        (await onPhone.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse("the passkey this session signed in with is gone");
        rig.Cleared(onPhone, IdentityConstants.ApplicationScheme).Should().BeTrue();
        (await onLaptop.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue("a different passkey signed this one in");
        (await withPassword.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue("removing a passkey is not changing the account's stamp");
    }

    /// <summary>
    /// A passkey sign-in that does not say which passkey could not be ended by revoking it, which is
    /// the one thing a lost device's owner can do - so it never becomes a session.
    /// </summary>
    [Fact]
    public async Task APasskeySignInThatNamesNoPasskeyIsRefused()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await SeedPasskeyAsync(rig, user);

        Func<Task> signIn = () => PasskeySessionCookieAsync(rig, user, credentialId: null);

        await signIn.Should().ThrowAsync<InvalidOperationException>();
        (await rig.Context.UserSessions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A password change elsewhere, a closed account: the stamp moves, and every session that was
    /// signed in under the old one is refused on its next request - no clock moves here.
    /// </summary>
    [Fact]
    public async Task ASessionWithAChangedStampIsEndedAtOnceAlongWithEverythingElse()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string session = await rig.SessionCookieAsync(user);
        string remembered = await rig.RememberedMachineCookieAsync(user);

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext later = rig.NewRequest(session, remembered);
        AuthenticateResult result = await later.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        result.Succeeded.Should().BeFalse("the account changed underneath this browser");
        rig.Cleared(later, IdentityConstants.ApplicationScheme).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeTrue("a stale stamp forgets the browser too");
    }

    /// <summary>
    /// The remembered cookie carries the stamp, so it is still honoured while the account is unchanged.
    /// Without the stamp claim the check would fail on the first read and, as the framework's validator
    /// does, end the session with it.
    /// </summary>
    [Fact]
    public async Task ARememberedBrowserWithTheSameStampIsStillRemembered()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string remembered = await rig.RememberedMachineCookieAsync(user);

        DefaultHttpContext later = rig.NewRequest(remembered);

        (await LocalSchemeRig.RulesOf(later).IsTwoFactorClientRememberedAsync(later, user)).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeFalse();
    }

    /// <summary>
    /// A remembered browser is what spares a sign-in its second factor, so a stamp change - an
    /// authenticator reset or turned off - forgets it on the very next read, not once an interval has
    /// passed. The clock does not move here: a browser remembered a minute ago is the case that matters.
    /// </summary>
    [Fact]
    public async Task ARememberedBrowserWithAChangedStampIsForgottenAtOnceAndTheSessionEnded()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string remembered = await rig.RememberedMachineCookieAsync(user);

        _clock.Advance(TimeSpan.FromMinutes(1));
        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext later = rig.NewRequest(remembered);

        (await LocalSchemeRig.RulesOf(later).IsTwoFactorClientRememberedAsync(later, user)).Should().BeFalse();
        rig.Cleared(later, IdentityConstants.TwoFactorRememberMeScheme).Should().BeTrue();
        rig.Cleared(later, IdentityConstants.ApplicationScheme).Should().BeTrue("the framework ends the session on a stale remembered browser, and so does this");
    }

    /// <summary>
    /// The application cookie for <paramref name="user"/> as a passkey sign-in writes it, naming
    /// <paramref name="credentialId"/> - or, when that is null, naming no passkey at all.
    /// </summary>
    private static async Task<string> PasskeySessionCookieAsync(LocalSchemeRig rig, HSUser user, string? credentialId)
    {
        DefaultHttpContext signIn = rig.NewRequest();
        ClaimsPrincipal principal = await rig.PrincipalOf(user);
        ClaimsIdentity identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, PasskeyAuthenticationHandler.AuthenticationMethod));

        if (credentialId is not null)
        {
            identity.AddClaim(new Claim(HSClaimTypes.PasskeyCredentialId, credentialId));
        }

        await LocalSchemeRig.SignInOf(signIn).SignInAsync(signIn, principal, isPersistent: false);

        return rig.CookieOf(signIn, IdentityConstants.ApplicationScheme);
    }

    private static async Task<UserPasskeyInfo> SeedPasskeyAsync(LocalSchemeRig rig, HSUser user)
    {
        UserPasskeyInfo passkey = new(
            credentialId: Guid.NewGuid().ToByteArray(),
            publicKey: [1, 2, 3],
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: null,
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: [],
            clientDataJson: []);

        (await rig.Users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();

        return passkey;
    }
}
