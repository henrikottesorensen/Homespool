using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A save runs the user validators when the username or the address changed, and not otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Every write the framework makes ends in the same validating save, and the validators check only
/// the name and the address. Before, an account whose stored name stopped validating - a Squint
/// update making two existing names lookalikes is the realistic way - could not spend a recovery code
/// or complete a passkey sign-in, and saw most of its other changes refused.
/// </para>
/// <para>
/// Each test sets the account up first and only then has the validator start refusing, which is the
/// shape of the real case: nothing about the account changed, the rules did.
/// </para>
/// </remarks>
public sealed class UpdateUserValidationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-updateuser-{Guid.NewGuid():N}.db");
    private readonly Refuser _refuser = new();

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
    /// The override's premise, pinned: these are the validators, and they check the name and the
    /// address. A new one that checks anything else fails this, and the override has to be revisited.
    /// </summary>
    [Fact]
    public async Task TheValidatorsAreTheOnesThatCheckOnlyTheNameAndTheAddress()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        // Act
        Type[] validators = [.. rig.Users.UserValidators.Select(validator => validator.GetType())];

        // Assert
        validators.Should().BeEquivalentTo([typeof(UserValidator<HSUser>), typeof(UsernameValidator), typeof(EmailAddressValidator)]);
    }

    [Fact]
    public async Task ARecoveryCodeIsSpentOnAnAccountTheValidatorsNowRefuse()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string[] codes = [.. (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!];
        _refuser.Refusing = true;

        // Act
        IdentityResult redeemed = await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0]);

        // Assert
        redeemed.Succeeded.Should().BeTrue();
        (await rig.Users.CountRecoveryCodesAsync(await ReloadAsync(rig, user))).Should().Be(2, "the code is spent, in the database");
    }

    [Fact]
    public async Task APasskeyUpdateSucceedsOnAnAccountTheValidatorsNowRefuse()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = new(Guid.NewGuid().ToByteArray(), [1, 2, 3], DateTimeOffset.UtcNow, 0, null, true, false, false, [], []) { Name = "laptop" };
        (await rig.Users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();
        _refuser.Refusing = true;
        passkey.SignCount = 7;

        // Act
        IdentityResult stored = await rig.Users.AddOrUpdatePasskeyAsync(user, passkey);

        // Assert
        stored.Succeeded.Should().BeTrue("a passkey sign-in fails closed on this result");
    }

    [Fact]
    public async Task TwoFactorIsTurnedOnForAnAccountTheValidatorsNowRefuse()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        _refuser.Refusing = true;

        // Act
        IdentityResult enabled = await rig.Users.SetTwoFactorEnabledAsync(user, true);

        // Assert
        enabled.Succeeded.Should().BeTrue();
        (await ReloadAsync(rig, user)).TwoFactorEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("someone-else")]
    [InlineData("OWNER")]
    public async Task ARenameIsStillValidated(string newName)
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string before = user.UserName!;
        _refuser.Refusing = true;

        // Act
        IdentityResult renamed = await rig.Users.SetUserNameAsync(user, newName);

        // Assert
        renamed.Succeeded.Should().BeFalse("a case-only change normalises to the same key and is still a rename");
        renamed.Errors.Should().Contain(error => error.Code == Refuser.Code);
        (await ReloadAsync(rig, user)).UserName.Should().Be(before);
    }

    [Fact]
    public async Task AChangedAddressIsStillValidated()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        _refuser.Refusing = true;
        user.Email = "elsewhere@example.com";

        // Act
        IdentityResult updated = await rig.Users.UpdateAsync(user);

        // Assert
        updated.Succeeded.Should().BeFalse();
        (await ReloadAsync(rig, user)).Email.Should().Be("owner@example.com");
    }

    /// <summary>An account the context is not tracking has nothing to compare with, so it is validated.</summary>
    [Fact]
    public async Task AnUntrackedAccountIsValidated()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUser untracked = await rig.Context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        rig.Context.ChangeTracker.Clear();
        _refuser.Refusing = true;

        // Act
        IdentityResult updated = await rig.Users.UpdateAsync(untracked);

        // Assert
        updated.Succeeded.Should().BeFalse();
    }

    /// <summary>The framework's validation refuses an account with no security stamp; skipping the validators keeps that.</summary>
    [Fact]
    public async Task AnAccountWithNoSecurityStampIsStillRefused()
    {
        // Arrange
        await using LocalSchemeRig rig = await RigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        user.SecurityStamp = null;

        // Act
        Func<Task> added = () => rig.Users.AddLoginAsync(user, new UserLoginInfo("oidc", "subject", "oidc"));

        // Assert
        await added.Should().ThrowAsync<InvalidOperationException>();
    }

    private Task<LocalSchemeRig> RigAsync()
    {
        return LocalSchemeRig.CreateAsync(_databasePath, services => services.AddSingleton<IUserValidator<HSUser>>(_refuser));
    }

    /// <summary>The account as the database holds it, rather than as the tracked entity says.</summary>
    private static async Task<HSUser> ReloadAsync(LocalSchemeRig rig, HSUser user)
    {
        rig.Context.ChangeTracker.Clear();

        return await rig.Context.Users.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
    }

    /// <summary>A validator that passes until told to refuse, so an account can be set up first.</summary>
    private sealed class Refuser : IUserValidator<HSUser>
    {
        public const string Code = "TestRefusal";

        public bool Refusing { get; set; }

        public Task<IdentityResult> ValidateAsync(UserManager<HSUser> manager, HSUser user)
        {
            return Task.FromResult(Refusing ? IdentityResult.Failed(new IdentityError { Code = Code }) : IdentityResult.Success);
        }
    }
}
