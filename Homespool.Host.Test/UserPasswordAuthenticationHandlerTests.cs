using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The password scheme: what it makes of a presented credential, which it refuses and why, and what it counts
/// toward the lockout - the framework's own rules, now in a handler a page can ask for by name.
/// </summary>
/// <remarks>
/// <b>The refusals are the tests that matter.</b> Each was checked by mutation: skipping the
/// pre-sign-in check, not counting a wrong password, or resetting the count regardless of an owed
/// second factor each turns its own tests red and nothing else.
/// </remarks>
public sealed class UserPasswordAuthenticationHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-userpw-{Guid.NewGuid():N}.db");

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

    private static UserPasswordCredential Credential(string? login, string? password)
    {
        return new UserPasswordCredential(login, password);
    }

    [Fact]
    public async Task ARequestWithNothingPresentedYieldsNoResult()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword);

        result.None.Should().BeTrue("no credential was presented, so the scheme has nothing to say");
    }

    [Fact]
    public async Task HalfACredentialIsRefusedRatherThanIgnored()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", null));

        result.Succeeded.Should().BeFalse();
        result.None.Should().BeFalse("a credential was presented, so this is a refusal");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner@example.com")]
    [InlineData("OWNER@EXAMPLE.COM")]
    public async Task AGoodPasswordAuthenticatesByUsernameOrAddress(string login)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(login, LocalSchemeRig.Password));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Ticket!.AuthenticationScheme.Should().Be(Schemes.UserPassword);
        ClaimsPrincipal principal = result.Principal!;
        principal.FindFirstValue(JwtClaimTypes.Subject).Should().Be(user.Id.ToString());
        principal.FindFirstValue(JwtClaimTypes.AuthenticationMethod).Should().Be(UserPasswordAuthenticationHandler.AuthenticationMethod);
    }

    [Fact]
    public async Task AnUnknownLoginIsRefusedLikeAWrongPassword()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        await rig.AddUserAsync("owner@example.com");

        AuthenticateResult unknown = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("nobody", LocalSchemeRig.Password));
        AuthenticateResult wrong = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", "not it")); // betterleaks:allow

        unknown.Succeeded.Should().BeFalse();
        wrong.Succeeded.Should().BeFalse();
        unknown.Refusal().Should().Be(SignInRefusal.Invalid);
        wrong.Refusal().Should().Be(SignInRefusal.Invalid);
        unknown.Failure!.Message.Should().Be(wrong.Failure!.Message, "the form must not say which identifiers exist");
    }

    [Fact]
    public async Task AWrongPasswordCountsTowardTheLockoutAndEnoughOfThemLockOut()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        int allowed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        AuthenticateResult last = AuthenticateResult.NoResult();

        for (int i = 0; i < allowed; i += 1)
        {
            last = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", "not it")); // betterleaks:allow
        }

        AuthenticateResult right = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", LocalSchemeRig.Password));

        (await rig.Users.IsLockedOutAsync(user)).Should().BeTrue();
        (await rig.Users.GetAccessFailedCountAsync(user)).Should().Be(0, "the store resets the count when it starts a lockout");
        last.Refusal().Should().Be(SignInRefusal.LockedOut, "the attempt that reached the limit reports the lockout it caused");
        right.Succeeded.Should().BeFalse("a locked-out account is refused before its password is even compared");
        right.Refusal().Should().Be(SignInRefusal.LockedOut);
    }

    [Fact]
    public async Task ARightPasswordResetsTheFailedCountWhenNoSecondFactorIsOwed()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");

        await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", "not it")); // betterleaks:allow
        await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", LocalSchemeRig.Password));

        (await rig.Users.GetAccessFailedCountAsync(user)).Should().Be(0);
    }

    /// <summary>
    /// The framework's quirk, kept on purpose: while the account still owes a code, a right password
    /// leaves the count standing, or an attacker holding the password would have it reset before
    /// every run at the code. A remembered machine owes no code, so it resets.
    /// </summary>
    [Fact]
    public async Task ARightPasswordLeavesTheFailedCountStandingWhileACodeIsStillOwed()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);

        await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", "not it")); // betterleaks:allow
        AuthenticateResult owing = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", LocalSchemeRig.Password));
        int standing = await rig.Users.GetAccessFailedCountAsync(user);

        string remembered = await rig.RememberedMachineCookieAsync(user);
        AuthenticateResult rememberedResult = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(remembered), Schemes.UserPassword, Credential("owner", LocalSchemeRig.Password));

        owing.Succeeded.Should().BeTrue();
        standing.Should().Be(1, "the code's own scheme resets it when the code is right");
        rememberedResult.Succeeded.Should().BeTrue();
        (await rig.Users.GetAccessFailedCountAsync(user)).Should().Be(0, "a remembered machine owes no code");
    }

    [Fact]
    public async Task AnUnconfirmedAccountIsNotAllowedEvenWithTheRightPassword()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        await rig.AddUserAsync("owner@example.com", confirmed: false);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential("owner", LocalSchemeRig.Password));

        result.Succeeded.Should().BeFalse();
        result.Refusal().Should().Be(SignInRefusal.NotAllowed);
    }

    [Fact]
    public async Task AChallengeAnswers401WithoutRedirecting()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        DefaultHttpContext request = rig.NewRequest();

        await request.ChallengeAsync(Schemes.UserPassword);

        request.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        request.Response.Headers.Location.ToString().Should().BeEmpty("the login page is where a password is asked for, and nothing routes there through this scheme");
    }
}
