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
        result.Properties!.Items[TotpAuthenticationHandler.SourceProperty].Should().Be(TotpAuthenticationHandler.PendingSource);
    }

    [Fact]
    public async Task ARightCodeAuthenticatesTheSignedInAccountOnAStepUp()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(
            rig.NewRequest(await rig.SessionCookieAsync(user)), Schemes.Totp, Code(LocalSchemeRig.CodeFor(secret)));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Properties!.Items[TotpAuthenticationHandler.SourceProperty].Should().Be(TotpAuthenticationHandler.SessionSource);
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
