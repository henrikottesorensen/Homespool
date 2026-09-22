using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Sessions as rows: a cookie signs somebody in only while the row it names is live, so ending a
/// session - signing out, revoking it, a sign-in replacing it - is final for that cookie, even replayed.
/// </summary>
/// <remarks>
/// Driven through the application cookie scheme over a migrated database, as
/// <see cref="StampValidatorTests"/> is, with one clock for the cookies, the rows and the validator.
/// </remarks>
public sealed class UserSessionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-session-{Guid.NewGuid():N}.db");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

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

    /// <summary>
    /// What a self-contained cookie could never do: after signing out, the same cookie presented again
    /// - copied off the machine beforehand, say - signs nobody in.
    /// </summary>
    [Fact]
    public async Task SigningOutEndsTheSessionSoItsCookieReplayedIsRefused()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);

        DefaultHttpContext signOut = rig.NewRequest(cookie);
        (await SignedInAsync(signOut)).Succeeded.Should().BeTrue();
        await LocalSchemeRig.SignInOf(signOut).SignOutAsync(signOut);

        DefaultHttpContext replay = rig.NewRequest(cookie);

        (await replay.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse("the row the cookie names is gone");
        (await RowsAsync(rig)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARevokedSessionIsRefusedOnItsNextRequestAndTheOwnersOtherSessionIsNot()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string laptop = await rig.SessionCookieAsync(user);
        _clock.Advance(TimeSpan.FromSeconds(1));
        string phone = await rig.SessionCookieAsync(user);

        UserSessionService sessions = SessionsOf(rig.NewRequest());
        IReadOnlyList<UserSession> live = await sessions.ListAsync(user.Id, TestContext.Current.CancellationToken);
        live.Should().HaveCount(2);

        // Newest first, so the phone's.
        (await sessions.RevokeAsync(user.Id, live[0].Uuid, TestContext.Current.CancellationToken)).Should().BeTrue();

        (await rig.NewRequest(phone).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse();
        (await rig.NewRequest(laptop).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ASessionCannotBeRevokedByAnotherAccount()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        string cookie = await rig.SessionCookieAsync(owner);

        UserSessionService sessions = SessionsOf(rig.NewRequest());
        Guid uuid = (await sessions.ListAsync(owner.Id, TestContext.Current.CancellationToken)).Single().Uuid;

        (await sessions.RevokeAsync(other.Id, uuid, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await rig.NewRequest(cookie).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Session fixation, which a ticket store driven by the cookie handler would have allowed: somebody
    /// with an account plants their own session cookie in a browser, and the victim signs in there. The
    /// victim's session must not be the planted one - a new secret and a new row - and the planted
    /// cookie must be dead rather than now answering as the victim.
    /// </summary>
    [Fact]
    public async Task ASignInEndsTheSessionTheBrowserArrivedWithAndStartsANewOne()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser attacker = await rig.AddUserAsync("attacker@example.com");
        HSUser victim = await rig.AddUserAsync("victim@example.com");
        string planted = await rig.SessionCookieAsync(attacker);

        DefaultHttpContext signIn = rig.NewRequest(planted);
        (await SignedInAsync(signIn)).Succeeded.Should().BeTrue();
        string plantedSecret = signIn.User.FindFirstValue(HSClaimTypes.SessionSecret)!;
        await LocalSchemeRig.SignInOf(signIn).SignInAsync(signIn, await rig.PrincipalOf(victim), isPersistent: false);
        string issued = rig.CookieOf(signIn, IdentityConstants.ApplicationScheme);

        AuthenticateResult asPlanted = await rig.NewRequest(planted).AuthenticateAsync(IdentityConstants.ApplicationScheme);
        AuthenticateResult asIssued = await rig.NewRequest(issued).AuthenticateAsync(IdentityConstants.ApplicationScheme);

        asPlanted.Succeeded.Should().BeFalse("the session the browser arrived with ended when somebody signed in there");
        asIssued.Succeeded.Should().BeTrue();
        rig.Users.GetUserId(asIssued.Principal!).Should().Be(victim.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        asIssued.Principal!.FindFirstValue(HSClaimTypes.SessionSecret).Should().NotBe(plantedSecret);
        (await RowsAsync(rig)).Should().ContainSingle().Which.UserId.Should().Be(victim.Id);
    }

    /// <summary>
    /// A password changed on one browser: that browser stays signed in, because the refresh brings its
    /// row to the new stamp, and every other browser is refused on its next request.
    /// </summary>
    [Fact]
    public async Task ARefreshAfterAStampChangeKeepsThisBrowserAndEndsTheOthers()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string here = await rig.SessionCookieAsync(user);
        string elsewhere = await rig.SessionCookieAsync(user);

        // As a page does it: the request is authenticated before the account changes, then refreshed.
        DefaultHttpContext change = rig.NewRequest(here);
        (await SignedInAsync(change)).Succeeded.Should().BeTrue();
        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
        (await LocalSchemeRig.SignInOf(change).RefreshSignInAsync(change, user)).Should().BeTrue();
        string refreshed = rig.CookieOf(change, IdentityConstants.ApplicationScheme);

        (await rig.NewRequest(refreshed).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();
        (await rig.NewRequest(elsewhere).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ARefreshOfASessionThatHasEndedIsRefused()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);

        DefaultHttpContext change = rig.NewRequest(cookie);
        (await SignedInAsync(change)).Succeeded.Should().BeTrue();
        await SessionsOf(change).RevokeAllAsync(user.Id, TestContext.Current.CancellationToken);

        (await LocalSchemeRig.SignInOf(change).RefreshSignInAsync(change, user)).Should().BeFalse();
        change.Response.Headers.SetCookie.Should().BeEmpty("a refused refresh writes no cookie that could outlive the revoke");
    }

    /// <summary>
    /// A cookie issued before sessions had rows - or one minted by anybody holding the data-protection
    /// keys - names no session, and signs nobody in.
    /// </summary>
    [Fact]
    public async Task ACookieNamingNoSessionIsRefused()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");

        CookieAuthenticationOptions options = rig.CookieOptions;
        AuthenticationTicket ticket = new(await rig.PrincipalOf(user),
                                          new AuthenticationProperties { IssuedUtc = _clock.GetUtcNow(), ExpiresUtc = _clock.GetUtcNow().AddHours(1) },
                                          IdentityConstants.ApplicationScheme);
        string cookie = $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";

        DefaultHttpContext request = rig.NewRequest(cookie);

        (await request.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse();
        rig.Cleared(request, IdentityConstants.ApplicationScheme).Should().BeTrue();
    }

    [Fact]
    public async Task AnExpiredRowIsRefusedWhileItsCookieIsStillValid()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);

        await rig.Context.UserSessions.ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ExpiresAt, _clock.GetUtcNow()),
                                                         TestContext.Current.CancellationToken);

        (await rig.NewRequest(cookie).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The row expires with its cookie and slides when the cookie does - past half its lifetime, when
    /// the handler renews it - and not before: a request early in the window writes nothing.
    /// </summary>
    [Fact]
    public async Task TheRowExpiresWithTheCookieAndSlidesOnlyWhenTheCookieDoes()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);
        TimeSpan length = rig.CookieOptions.ExpireTimeSpan;
        DateTimeOffset signedIn = _clock.GetUtcNow();

        (await RowsAsync(rig)).Single().ExpiresAt.Should().Be(signedIn + length);

        _clock.Advance(length / 4);
        DefaultHttpContext early = rig.NewRequest(cookie);
        (await early.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();

        (await RowsAsync(rig)).Single().ExpiresAt.Should().Be(signedIn + length, "the cookie has not slid, so neither has the row");

        _clock.Advance(length / 2);
        DefaultHttpContext late = rig.NewRequest(cookie);
        (await late.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();

        (await RowsAsync(rig)).Single().ExpiresAt.Should().Be(_clock.GetUtcNow() + length, "the handler renews the cookie past half its life, and the row follows it");
    }

    /// <summary>
    /// The slide runs before the row is checked, so it has to check for itself: a dead row is not given
    /// a longer life - which would keep it from the sweep - by the cookie that is about to be refused.
    /// </summary>
    [Fact]
    public async Task ExtendingLeavesADeadRowAlone()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);

        DefaultHttpContext request = rig.NewRequest(cookie);
        (await SignedInAsync(request)).Succeeded.Should().BeTrue();
        string secret = request.User.FindFirstValue(HSClaimTypes.SessionSecret)!;
        DateTimeOffset expires = (await RowsAsync(rig)).Single().ExpiresAt;

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
        await SessionsOf(request).ExtendAsync(secret, expires.AddDays(30), TestContext.Current.CancellationToken);

        (await RowsAsync(rig)).Single().ExpiresAt.Should().Be(expires);
    }

    /// <summary>
    /// The sweep deletes exactly what a request would refuse - expired, stale-stamped, or naming a
    /// passkey that is gone - and nothing a request would accept.
    /// </summary>
    [Fact]
    public async Task TheSweepRemovesEndedSessionsAndKeepsLiveOnes()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser live = await rig.AddUserAsync("live@example.com");
        HSUser restamped = await rig.AddUserAsync("restamped@example.com");
        HSUser expired = await rig.AddUserAsync("expired@example.com");
        HSUser lostDevice = await rig.AddUserAsync("lost@example.com");

        string keep = await rig.SessionCookieAsync(live);
        await rig.SessionCookieAsync(restamped);
        await rig.SessionCookieAsync(expired);
        UserPasskeyInfo phone = await SeedPasskeyAsync(rig, lostDevice);
        await PasskeySessionCookieAsync(rig, lostDevice, phone);

        (await rig.Users.UpdateSecurityStampAsync(restamped)).Succeeded.Should().BeTrue();
        await rig.Context.UserSessions.Where(session => session.UserId == expired.Id)
                 .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ExpiresAt, _clock.GetUtcNow()),
                                     TestContext.Current.CancellationToken);
        await rig.Users.RemovePasskeyAsync(lostDevice, phone.CredentialId);

        int swept = await SessionsOf(rig.NewRequest()).SweepAsync(TestContext.Current.CancellationToken);

        swept.Should().Be(3);
        (await RowsAsync(rig)).Should().ContainSingle().Which.UserId.Should().Be(live.Id);
        (await rig.NewRequest(keep).AuthenticateAsync(IdentityConstants.ApplicationScheme)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task TheRowKeepsOnlyTheSecretsHash()
    {
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string cookie = await rig.SessionCookieAsync(user);

        DefaultHttpContext request = rig.NewRequest(cookie);
        (await SignedInAsync(request)).Succeeded.Should().BeTrue();
        string secret = request.User.FindFirstValue(HSClaimTypes.SessionSecret)!;

        UserSession row = (await RowsAsync(rig)).Single();
        row.SecretHash.Should().Be(Accounts.ApiTokenService.HashSecret(secret)).And.NotBe(secret);
        Base64Url.DecodeFromChars(secret).Should().HaveCount(UserSessionService.SecretByteCount);
    }

    private Task<LocalSchemeRig> RigAsync()
    {
        return LocalSchemeRig.CreateAsync(_databasePath, services => services.AddSingleton<TimeProvider>(_clock));
    }

    /// <summary>Authenticates the application cookie and, when it succeeds, signs the request in, as the middleware would.</summary>
    private static async Task<AuthenticateResult> SignedInAsync(DefaultHttpContext request)
    {
        AuthenticateResult result = await request.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        if (result.Succeeded)
        {
            request.User = result.Principal!;
        }

        return result;
    }

    private static UserSessionService SessionsOf(DefaultHttpContext request)
    {
        return request.RequestServices.GetRequiredService<UserSessionService>();
    }

    private static async Task<List<UserSession>> RowsAsync(LocalSchemeRig rig)
    {
        return await rig.Context.UserSessions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task PasskeySessionCookieAsync(LocalSchemeRig rig, HSUser user, UserPasskeyInfo passkey)
    {
        DefaultHttpContext signIn = rig.NewRequest();
        ClaimsPrincipal principal = await rig.PrincipalOf(user);
        ClaimsIdentity identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, PasskeyAuthenticationHandler.AuthenticationMethod));
        identity.AddClaim(new Claim(HSClaimTypes.PasskeyCredentialId, Base64Url.EncodeToString(passkey.CredentialId)));

        await LocalSchemeRig.SignInOf(signIn).SignInAsync(signIn, principal, isPersistent: false);
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
