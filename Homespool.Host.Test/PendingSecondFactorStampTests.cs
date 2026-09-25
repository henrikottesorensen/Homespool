using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A passed first factor is bound to the account as it was when it passed: a new password, a reset
/// authenticator or a removed login - anything that moves the security stamp - forgets it.
/// </summary>
/// <remarks>
/// The threat is a phished password. Without the binding, whoever passed the first factor keeps a
/// pending sign-in however the owner answers, and can go on guessing codes against it.
/// </remarks>
public sealed class PendingSecondFactorStampTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pending-{Guid.NewGuid():N}.db");

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
    public async Task ARightCodeThroughAPendingSignInFromBeforeTheStampMovedIsRefused()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string pending = await rig.PendingTwoFactorCookieAsync(user);

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext request = rig.NewRequest(pending);
        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(request, Schemes.Totp, new TotpCredential(LocalSchemeRig.CodeFor(secret)));

        result.Succeeded.Should().BeFalse("the first factor was passed by an account that has since changed");
        rig.Cleared(request, IdentityConstants.TwoFactorUserIdScheme).Should().BeTrue("a stale pending sign-in is forgotten, not just ignored");
    }

    [Fact]
    public async Task ARecoveryCodeThroughAPendingSignInFromBeforeTheStampMovedIsRefused()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);
        string code = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.Single();
        string pending = await rig.PendingTwoFactorCookieAsync(user);

        (await rig.Users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();

        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(pending), Schemes.RecoveryCode, new RecoveryCodeCredential(code));

        result.Succeeded.Should().BeFalse();
        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(1, "a refused pending sign-in spends nothing");
    }

    [Fact]
    public async Task ACurrentPendingSignInThroughAProviderStillTakesItsCode()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string pending = await rig.PendingTwoFactorCookieAsync(user, loginProvider: "oidc");

        DefaultHttpContext request = rig.NewRequest(pending);
        AuthenticateResult result = await LocalSchemeRig.AuthenticateAsync(request, Schemes.Totp, new TotpCredential(LocalSchemeRig.CodeFor(secret)));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        (await LocalSchemeRig.RulesOf(request).PendingLoginProviderAsync(request)).Should().Be("oidc");
    }

    /// <summary>
    /// A browser can hold one account's session and another's pending sign-in. The pending one going
    /// stale says nothing about the session, which must survive it.
    /// </summary>
    [Fact]
    public async Task AStalePendingSignInLeavesAnotherAccountsSessionAlone()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser signedIn = await rig.AddUserAsync("signed-in@example.com");
        HSUser pendingUser = await rig.AddUserAsync("pending@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(pendingUser);
        string session = await rig.SessionCookieAsync(signedIn);
        string pending = await rig.PendingTwoFactorCookieAsync(pendingUser);

        (await rig.Users.UpdateSecurityStampAsync(pendingUser)).Succeeded.Should().BeTrue();

        DefaultHttpContext request = rig.NewRequest(session, pending);
        await LocalSchemeRig.AuthenticateAsync(request, Schemes.Totp, new TotpCredential(LocalSchemeRig.CodeFor(secret)));

        rig.Cleared(request, IdentityConstants.TwoFactorUserIdScheme).Should().BeTrue();
        rig.Cleared(request, IdentityConstants.ApplicationScheme).Should().BeFalse("the session belongs to another account, which did not change");
    }
}
