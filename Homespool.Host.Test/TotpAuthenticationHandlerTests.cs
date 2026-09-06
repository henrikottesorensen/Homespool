using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The code schemes: an authenticator code verified for the account the pending cookie or the
/// session names, and never for nobody; and a recovery code redeemed for the pending account alone.
/// </summary>
public sealed class TotpAuthenticationHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-totp-{Guid.NewGuid():N}.db");

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

    private static TotpCredential Code(string code)
    {
        return new TotpCredential(code);
    }

    private static TotpStepUpCredential StepUp(string code)
    {
        return new TotpStepUpCredential(code);
    }

    private static RecoveryCodeCredential Recovery(string code)
    {
        return new RecoveryCodeCredential(code);
    }

    // ---------- the authenticator code ----------
    [Fact]
    public async Task ACodeWithNoAccountToVerifyItForIsRefused()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.Totp, Code(LocalSchemeRig.CodeFor(secret)));

        result.Succeeded.Should().BeFalse("a code identifies nobody, so with no first factor there is nobody to verify it for");
        result.None.Should().BeFalse();
    }

    [Fact]
    public async Task NoCodeYieldsNoResult()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(await rig.PendingTwoFactorCookieAsync(user)), Schemes.Totp);

        result.None.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARightCodeAuthenticatesThePendingAccount(bool withSeparators)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string code = LocalSchemeRig.CodeFor(secret);
        string typed = withSeparators ? $"{code[..3]} {code[3..]}" : code;

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(
            rig.NewRequest(await rig.PendingTwoFactorCookieAsync(user)), Schemes.Totp, Code(typed));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        ClaimsPrincipal principal = result.Principal!;
        principal.FindFirstValue(JwtClaimTypes.Subject).Should().Be(user.Id.ToString());
        principal.FindFirstValue(JwtClaimTypes.AuthenticationMethod).Should().Be(TotpAuthenticationHandler.AuthenticationMethod);
    }

    [Fact]
    public async Task AStepUpCodeAuthenticatesTheSignedInAccount()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string session = await rig.SessionCookieAsync(user);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session), Schemes.Totp, StepUp(LocalSchemeRig.CodeFor(secret)));
        AuthenticateResult asSignIn = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session), Schemes.Totp, Code(LocalSchemeRig.CodeFor(secret)));
        AuthenticateResult nobody = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.Totp, StepUp(LocalSchemeRig.CodeFor(secret)));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(JwtClaimTypes.Subject).Should().Be(user.Id.ToString());
        asSignIn.Succeeded.Should().BeFalse("a sign-in code is for the pending account, and none is pending");
        nobody.Succeeded.Should().BeFalse("a step-up code is for the signed-in account, and nobody is signed in");
    }

    /// <summary>
    /// One browser, two accounts: signed in as the owner, with the other account's password step
    /// left pending. Each credential verifies against its own account and no other, so the other
    /// account's code cannot pass the owner's step-up.
    /// </summary>
    [Fact]
    public async Task EachCredentialVerifiesAgainstItsOwnAccountWhenABrowserHoldsTwo()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        byte[] ownerSecret = await rig.EnableAuthenticatorAsync(owner);
        byte[] otherSecret = await rig.EnableAuthenticatorAsync(other);
        string session = await rig.SessionCookieAsync(owner);
        string pending = await rig.PendingTwoFactorCookieAsync(other);

        AuthenticateResult ownersStepUp = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session, pending), Schemes.Totp, StepUp(LocalSchemeRig.CodeFor(ownerSecret)));
        AuthenticateResult othersCodeAsStepUp = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session, pending), Schemes.Totp, StepUp(LocalSchemeRig.CodeFor(otherSecret)));
        AuthenticateResult othersSignIn = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session, pending), Schemes.Totp, Code(LocalSchemeRig.CodeFor(otherSecret)));
        AuthenticateResult both = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(session, pending), Schemes.Totp, Code(LocalSchemeRig.CodeFor(otherSecret)), StepUp(LocalSchemeRig.CodeFor(ownerSecret)));

        ownersStepUp.Succeeded.Should().BeTrue(ownersStepUp.Failure?.Message);
        ownersStepUp.Principal!.FindFirstValue(JwtClaimTypes.Subject).Should().Be(owner.Id.ToString());
        othersCodeAsStepUp.Succeeded.Should().BeFalse("the other account's code is not the owner's, whatever else the browser holds");
        othersSignIn.Succeeded.Should().BeTrue(othersSignIn.Failure?.Message);
        othersSignIn.Principal!.FindFirstValue(JwtClaimTypes.Subject).Should().Be(other.Id.ToString());
        both.Succeeded.Should().BeFalse("one call, one account");
    }

    [Fact]
    public async Task AWrongCodeCountsTowardTheLockoutAndARightOneResetsIt()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string pending = await rig.PendingTwoFactorCookieAsync(user);

        AuthenticateResult wrong = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.Totp, Code("000000"));
        int counted = await rig.Users.GetAccessFailedCountAsync(user);
        AuthenticateResult right = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.Totp, Code(LocalSchemeRig.CodeFor(secret)));

        wrong.Succeeded.Should().BeFalse();
        wrong.Refusal().Should().Be(SignInRefusal.Invalid);
        counted.Should().Be(1);
        right.Succeeded.Should().BeTrue();
        (await rig.Users.GetAccessFailedCountAsync(user)).Should().Be(0);
    }

    [Fact]
    public async Task EnoughWrongCodesLockTheAccountOut()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);
        string pending = await rig.PendingTwoFactorCookieAsync(user);
        AuthenticateResult last = AuthenticateResult.NoResult();

        for (int i = 0; i < rig.Users.Options.Lockout.MaxFailedAccessAttempts; i += 1)
        {
            last = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.Totp, Code("000000"));
        }

        last.Refusal().Should().Be(SignInRefusal.LockedOut);
    }

    // ---------- the recovery code ----------
    [Fact]
    public async Task ARecoveryCodeIsRedeemedForThePendingAccountAndOnlyOnce()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);
        string code = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();
        string pending = await rig.PendingTwoFactorCookieAsync(user);

        AuthenticateResult first = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.RecoveryCode, Recovery(code));
        AuthenticateResult again = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.RecoveryCode, Recovery(code));

        first.Succeeded.Should().BeTrue(first.Failure?.Message);
        first.Principal!.FindFirstValue(JwtClaimTypes.Subject).Should().Be(user.Id.ToString());
        again.Succeeded.Should().BeFalse("a redeemed code is spent");
    }

    /// <summary>A recovery code is for getting back in, not for confirming an act: the session is not a source for it.</summary>
    [Fact]
    public async Task ARecoveryCodeIsNotAcceptedOnAStepUp()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);
        string code = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(await rig.SessionCookieAsync(user)), Schemes.RecoveryCode, Recovery(code));

        result.Succeeded.Should().BeFalse();
    }
}
