using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="HSUserManager.RemovePasskeyAsync"/> fails for a passkey the account does not hold, and
/// writes nothing when it does.
/// </summary>
/// <remarks>
/// The framework reports that removal as a success: the store finds nothing and removes nothing, and
/// the account row is saved regardless.
/// </remarks>
public sealed class RemovePasskeyTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-removepasskey-{Guid.NewGuid():N}.db");

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
    public async Task RemovingAHeldPasskeyRemovesIt()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = await SeedPasskeyAsync(rig, user);

        // Act
        IdentityResult result = await rig.Users.RemovePasskeyAsync(user, passkey.CredentialId);

        // Assert
        rig.Users.Should().BeOfType<HSUserManager>("the override is what is under test");
        result.Succeeded.Should().BeTrue();
        (await rig.Users.GetPasskeysAsync(user)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAPasskeyTheAccountDoesNotHoldFailsAndWritesNothing()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        await SeedPasskeyAsync(rig, user);
        string before = await StoredConcurrencyStampAsync(rig, user);

        // Act
        IdentityResult result = await rig.Users.RemovePasskeyAsync(user, Guid.NewGuid().ToByteArray());

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(HSUserManager.PasskeyNotHeldCode);
        (await rig.Users.GetPasskeysAsync(user)).Should().ContainSingle();
        (await StoredConcurrencyStampAsync(rig, user)).Should().Be(before, "the account row was not saved either");
    }

    /// <summary>A credential id names one passkey anywhere, so another account's names nothing on this one - and stays on its own.</summary>
    [Fact]
    public async Task AnotherAccountsPasskeyIsNotRemovedThroughThisOne()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        UserPasskeyInfo theirs = await SeedPasskeyAsync(rig, other);

        // Act
        IdentityResult result = await rig.Users.RemovePasskeyAsync(user, theirs.CredentialId);

        // Assert
        result.Succeeded.Should().BeFalse();
        (await rig.Users.GetPasskeysAsync(other)).Should().ContainSingle();
    }

    private static async Task<UserPasskeyInfo> SeedPasskeyAsync(LocalSchemeRig rig, HSUser user)
    {
        UserPasskeyInfo passkey = new(Guid.NewGuid().ToByteArray(), [1, 2, 3], DateTimeOffset.UtcNow, 0, null, true, false, false, [], []) { Name = "laptop" };
        (await rig.Users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();

        return passkey;
    }

    /// <summary>The concurrency stamp as the database holds it; a save of the account row moves it.</summary>
    private static async Task<string> StoredConcurrencyStampAsync(LocalSchemeRig rig, HSUser user)
    {
        return (await rig.Context.Users.AsNoTracking()
                         .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken)).ConcurrencyStamp!;
    }
}
