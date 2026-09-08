using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What an administrator may do to an account, and the two things they may not.
/// </summary>
/// <remarks>
/// <b>The refusals are what this file is for.</b> A service that acted on everything would pass any
/// test asserting that deactivation deactivates, so the guards - your own account, the last
/// administrator standing - carry their own tests, and each was checked by removing the guard and
/// watching exactly one of them fail.
/// </remarks>
public sealed class UserAdministrationTests : IDisposable
{
    private const string Password = "Correct horse battery staple 1"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-useradmin-{Guid.NewGuid():N}.db");

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
    public async Task DeactivatingClosesTheAccountAndTakesItsTokensWithIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        await tokens.CreateAsync(subject.Id, "pi", [Capability.Print], CancellationToken.None);
        string stampBefore = subject.SecurityStamp!;

        // Act
        UserAdminResult result = await Administration(context, provider)
            .DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Affected.Should().Be(2, "the count is what the page tells the administrator");
        subject.DeactivatedAt.Should().NotBeNull();
        subject.SecurityStamp.Should().NotBe(stampBefore, "the sessions the account already has must end");
        (await tokens.ListAsync(subject.Id, CancellationToken.None)).Should().BeEmpty();
    }

    /// <summary>
    /// The whole point of the column: one condition in <see cref="LocalSignInRules.CanSignInAsync"/>,
    /// which every credential scheme in the application asks before it believes anything.
    /// </summary>
    [Fact]
    public async Task ADeactivatedAccountMayNotSignIn()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        LocalSignInRules rules = provider.GetRequiredService<LocalSignInRules>();

        // Act
        SignInRefusal? before = await rules.PreSignInCheckAsync(subject);
        await Administration(context, provider).DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);
        SignInRefusal? after = await rules.PreSignInCheckAsync(subject);

        // Assert
        before.Should().BeNull("the account was confirmed and unlocked");
        after.Should().Be(SignInRefusal.NotAllowed);
    }

    [Fact]
    public async Task AnAdministratorCannotDeactivateTheirOwnAccount()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser other = await AddUserAsync(users, "other@example.com");
        await MakeAdministratorAsync(provider, users, admin);
        await MakeAdministratorAsync(provider, users, other);

        // Act
        UserAdminResult result = await Administration(context, provider)
            .DeactivateAsync(admin.Id, admin.Id, CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(UserAdminRefusal.Self);
        admin.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public async Task TheLastActiveAdministratorCannotBeDeactivated()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser deputy = await AddUserAsync(users, "deputy@example.com");
        await MakeAdministratorAsync(provider, users, admin);
        await MakeAdministratorAsync(provider, users, deputy);
        UserAdministration administration = Administration(context, provider);

        // Act
        UserAdminResult first = await administration.DeactivateAsync(deputy.Id, admin.Id, CancellationToken.None);
        UserAdminResult second = await administration.DeactivateAsync(admin.Id, deputy.Id, CancellationToken.None);

        // Assert
        first.Succeeded.Should().BeTrue("two administrators were active, so closing one leaves one");
        second.Refusal.Should().Be(UserAdminRefusal.LastAdministrator,
                                   "the first deactivation left this account as the only administrator who can sign in");
        deputy.DeactivatedAt.Should().BeNull();
    }

    /// <summary>
    /// An ordinary account is not covered by the administrator guard, however few of them there are -
    /// otherwise the last user on a deployment could never be closed.
    /// </summary>
    [Fact]
    public async Task TheGuardCountsAdministratorsRatherThanAccounts()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        await MakeAdministratorAsync(provider, users, admin);

        // Act
        UserAdminResult result = await Administration(context, provider)
            .DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        subject.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReactivatingReopensTheAccountAndDoesNotBringItsTokensBack()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        UserAdministration administration = Administration(context, provider);
        await administration.DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        // Act
        UserAdminResult result = await administration.ReactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        subject.DeactivatedAt.Should().BeNull();
        (await provider.GetRequiredService<LocalSignInRules>().PreSignInCheckAsync(subject)).Should().BeNull();
        (await tokens.ListAsync(subject.Id, CancellationToken.None))
            .Should().BeEmpty("a reopened account must not silently hand a compromise back its credentials");
    }

    [Fact]
    public async Task RevokingTokensTakesEveryOneOfThemAndLeavesOtherAccountsAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        HSUser bystander = await AddUserAsync(users, "bystander@example.com");
        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        await tokens.CreateAsync(bystander.Id, "theirs", [Capability.Print], CancellationToken.None);

        // Act
        UserAdminResult result = await Administration(context, provider)
            .RevokeTokensAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Affected.Should().Be(1);
        (await tokens.ListAsync(subject.Id, CancellationToken.None)).Should().BeEmpty();
        (await tokens.ListAsync(bystander.Id, CancellationToken.None)).Should().ContainSingle();
        subject.DeactivatedAt.Should().BeNull("revoking tokens is not closing the account");
    }

    /// <summary>
    /// Both halves of being held out: the lockout a wrong password builds, and the backoffs a flood
    /// of reset mail builds - which the account's owner cannot clear by succeeding at anything.
    /// </summary>
    [Fact]
    public async Task ClearingTheLockoutLiftsTheLockoutAndEveryBackoff()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        AttemptLimiter limiter = provider.GetRequiredService<AttemptLimiter>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int attempt = 0; attempt < 9; attempt++)
        {
            await limiter.RecordFailedAttemptAsync(subject.Id, LimitedAction.SendPasswordResetEmail, now, CancellationToken.None);
            await users.AccessFailedAsync(subject);
        }

        (await users.IsLockedOutAsync(subject)).Should().BeTrue("the fixture has to reach the state being cleared");
        (await limiter.RemainingLockoutAsync(subject.Id, LimitedAction.SendPasswordResetEmail, now, CancellationToken.None))
            .Should().NotBeNull();

        // Act
        UserAdminResult result = await Administration(context, provider)
            .ClearLockoutAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Affected.Should().Be(1, "one backoff row was standing");
        (await users.IsLockedOutAsync(subject)).Should().BeFalse();
        subject.AccessFailedCount.Should().Be(0);
        (await limiter.RemainingLockoutAsync(subject.Id, LimitedAction.SendPasswordResetEmail, now, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ActingOnAnAccountThatIsNotThereRefusesRatherThanThrows()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        UserAdministration administration = Administration(context, provider);

        // Act
        UserAdminResult deactivate = await administration.DeactivateAsync(admin.Id, 9999, CancellationToken.None);
        UserAdminResult revoke = await administration.RevokeTokensAsync(admin.Id, 9999, CancellationToken.None);
        UserAdminResult lockout = await administration.ClearLockoutAsync(admin.Id, 9999, CancellationToken.None);

        // Assert
        deactivate.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
        revoke.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
        lockout.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
    }

    private static UserAdministration Administration(HomespoolDbContext context, IServiceProvider provider)
    {
        return new UserAdministration(context,
                                      new ApiTokenService(context),
                                      provider.GetRequiredService<AttemptLimiter>(),
                                      new UnitOfWork(context),
                                      TimeProvider.System,
                                      NullLogger<UserAdministration>.Instance);
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>
    /// Puts <paramref name="user"/> in the administrator role, seeding the role itself the way
    /// <see cref="AdminBootstrap"/> does on a first start.
    /// </summary>
    private static async Task MakeAdministratorAsync(IServiceProvider provider, UserManager<HSUser> users, HSUser user)
    {
        RoleManager<IdentityRole<long>> roles = provider.GetRequiredService<RoleManager<IdentityRole<long>>>();

        if (!await roles.RoleExistsAsync(AdminBootstrap.AdminRole))
        {
            (await roles.CreateAsync(new IdentityRole<long>(AdminBootstrap.AdminRole))).Succeeded.Should().BeTrue();
        }

        (await users.AddToRoleAsync(user, AdminBootstrap.AdminRole)).Succeeded.Should().BeTrue();
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
