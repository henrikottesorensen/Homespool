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
/// <see cref="HSUserManager.RemoveLoginAsync"/> fails for a login the account does not hold, and
/// writes nothing when it does.
/// </summary>
/// <remarks>
/// The framework reports that removal as a success, having rotated the stamp and saved; a caller that
/// sets a password on the strength of it leaves the account with a password and its provider both.
/// </remarks>
public sealed class RemoveLoginTests : IDisposable
{
    private const string Provider = "oidc";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-removelogin-{Guid.NewGuid():N}.db");

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
    public async Task RemovingAHeldLoginRemovesItAndRotatesTheStamp()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await AccountWithLoginAsync(rig, "owner@example.com", "subject");
        string before = await StoredStampAsync(rig, user);

        // Act
        IdentityResult result = await rig.Users.RemoveLoginAsync(user, Provider, "subject");

        // Assert
        rig.Users.Should().BeOfType<HSUserManager>("the override is what is under test");
        result.Succeeded.Should().BeTrue();
        (await rig.Users.GetLoginsAsync(user)).Should().BeEmpty();

        string after = await StoredStampAsync(rig, user);
        after.Should().NotBe(before).And.MatchRegex("^[A-Z2-7]{32}$", "the framework's shape: twenty bytes as unpadded base32");
    }

    [Theory]
    [InlineData("elsewhere", "subject")]
    [InlineData(Provider, "somebody")]
    [InlineData("OIDC", "subject")]
    public async Task RemovingALoginTheAccountDoesNotHoldFailsAndWritesNothing(string loginProvider, string providerKey)
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await AccountWithLoginAsync(rig, "owner@example.com", "subject");
        string before = await StoredStampAsync(rig, user);

        // Act
        IdentityResult result = await rig.Users.RemoveLoginAsync(user, loginProvider, providerKey);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(HSUserManager.LoginNotHeldCode);
        (await rig.Users.GetLoginsAsync(user)).Should().ContainSingle();
        (await StoredStampAsync(rig, user)).Should().Be(before, "nothing changed, so no session is ended over it");
    }

    /// <summary>A login is named by its pair, so another account's pair names nothing on this one - and stays on its own.</summary>
    [Fact]
    public async Task AnotherAccountsLoginIsNotRemovedThroughThisOne()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await AccountWithLoginAsync(rig, "owner@example.com", "subject");
        HSUser other = await AccountWithLoginAsync(rig, "other@example.com", "theirs");

        // Act
        IdentityResult result = await rig.Users.RemoveLoginAsync(user, Provider, "theirs");

        // Assert
        result.Succeeded.Should().BeFalse();
        (await rig.Users.GetLoginsAsync(other)).Should().ContainSingle();
    }

    private static async Task<HSUser> AccountWithLoginAsync(LocalSchemeRig rig, string email, string subject)
    {
        HSUser user = await rig.AddUserAsync(email);
        (await rig.Users.AddLoginAsync(user, new UserLoginInfo(Provider, subject, Provider))).Succeeded.Should().BeTrue();

        return user;
    }

    /// <summary>The stamp as the database holds it, rather than as the tracked entity says.</summary>
    private static async Task<string> StoredStampAsync(LocalSchemeRig rig, HSUser user)
    {
        return (await rig.Context.Users.AsNoTracking()
                         .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken)).SecurityStamp!;
    }
}
