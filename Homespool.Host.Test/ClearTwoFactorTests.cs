using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="HSUserManager.ClearTwoFactorAsync"/> leaves nothing of the old second factor that
/// answers: the flag is off, the key is new, and the recovery codes are gone.
/// </summary>
/// <remarks>
/// The codes are the half that is easy to miss. Enabling an authenticator mints a set only for an
/// account with none, so codes that outlive the clear come back into force beside the new key.
/// </remarks>
public sealed class ClearTwoFactorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-cleartwofactor-{Guid.NewGuid():N}.db");

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
    public async Task ClearingTurnsTwoFactorOffRekeysAndSpendsEveryRecoveryCode()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await rig.EnableAuthenticatorAsync(user);

        List<string> codes = [.. await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? []];
        codes.Should().HaveCount(10, "the account starts with a full set to lose");

        string keyBefore = (await rig.Users.GetAuthenticatorKeyAsync(user))!;
        string stampBefore = await StoredStampAsync(rig, user);

        HSUserManager users = rig.Users.Should().BeOfType<HSUserManager>("the method is what is under test").Subject;

        // Act
        IdentityResult result = await users.ClearTwoFactorAsync(user);

        // Assert
        result.Succeeded.Should().BeTrue();
        (await rig.Users.GetTwoFactorEnabledAsync(user)).Should().BeFalse();
        (await rig.Users.GetAuthenticatorKeyAsync(user)).Should().NotBeNullOrEmpty().And.NotBe(keyBefore);
        (await rig.Users.CountRecoveryCodesAsync(user))
            .Should().Be(0, "none left is what makes the next enable mint and show a fresh set");
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded
            .Should().BeFalse("an old code is part of the second factor that was cleared");

        (await StoredStampAsync(rig, user))
            .Should().NotBe(stampBefore, "the sessions that held the old second factor end with it");
    }

    /// <summary>The stamp as the database holds it, rather than as the tracked entity says.</summary>
    private static async Task<string> StoredStampAsync(LocalSchemeRig rig, HSUser user)
    {
        return (await rig.Context.Users.AsNoTracking()
                         .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken)).SecurityStamp!;
    }
}
