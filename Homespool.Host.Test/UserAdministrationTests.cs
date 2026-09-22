using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

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
/// administrator standing, an administrator who is closed themselves - carry their own tests, and
/// each was checked by removing the guard and watching exactly one of them fail.
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

    /// <summary>
    /// The second call is made by an ordinary account, because nobody who can reach the button is
    /// left to make it once one administrator remains: that administrator is refused as themselves,
    /// and a closed one is refused before it aims. The guard is a property of the act all the same,
    /// for a caller that is not the page.
    /// </summary>
    [Fact]
    public async Task TheLastActiveAdministratorCannotBeDeactivated()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser deputy = await AddUserAsync(users, "deputy@example.com");
        HSUser clerk = await AddUserAsync(users, "clerk@example.com");
        await MakeAdministratorAsync(provider, users, admin);
        await MakeAdministratorAsync(provider, users, deputy);
        UserAdministration administration = Administration(context, provider);

        // Act
        UserAdminResult first = await administration.DeactivateAsync(admin.Id, deputy.Id, CancellationToken.None);
        UserAdminResult second = await administration.DeactivateAsync(clerk.Id, admin.Id, CancellationToken.None);

        // Assert
        first.Succeeded.Should().BeTrue("two administrators were active, so closing one leaves one");
        second.Refusal.Should().Be(UserAdminRefusal.LastAdministrator,
                                   "the first deactivation left this account as the only administrator who can sign in");
        admin.DeactivatedAt.Should().BeNull();
    }

    /// <summary>
    /// A closed administrator's session outlives the closure until its stamp is next re-checked, and
    /// the cookie still carries the role for that window. The service reads the row, so every act it
    /// offers refuses - reopening themselves, reopening anybody else, and the three that touch an
    /// open account - and the accounts aimed at are exactly as they were.
    /// </summary>
    [Fact]
    public async Task AClosedAdministratorIsRefusedEveryAct()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser deputy = await AddUserAsync(users, "deputy@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        HSUser stranger = await AddUserAsync(users, "stranger@example.com");
        await MakeAdministratorAsync(provider, users, admin);
        await MakeAdministratorAsync(provider, users, deputy);
        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, subject, "phone");
        CapturingEmailSender mail = new();
        UserAdministration administration = Administration(context, provider, mail: mail);

        for (int attempt = 0; attempt < 9; attempt++)
        {
            await users.AccessFailedAsync(subject);
        }

        (await administration.DeactivateAsync(admin.Id, stranger.Id, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await administration.DeactivateAsync(admin.Id, deputy.Id, CancellationToken.None)).Succeeded.Should().BeTrue(
            "two administrators were active, so closing one leaves one");

        // Act
        UserAdminResult reopenSelf = await administration.ReactivateAsync(deputy.Id, deputy.Id, CancellationToken.None);
        UserAdminResult reopenOther = await administration.ReactivateAsync(deputy.Id, stranger.Id, CancellationToken.None);
        UserAdminResult deactivate = await administration.DeactivateAsync(deputy.Id, subject.Id, CancellationToken.None);
        UserAdminResult revoke = await administration.RevokeTokensAsync(deputy.Id, subject.Id, CancellationToken.None);
        UserAdminResult passkey = await administration.RevokePasskeyAsync(deputy.Id, subject.Id, phone.CredentialId, CancellationToken.None);
        UserAdminResult lockout = await administration.ClearLockoutAsync(deputy.Id, subject.Id, CancellationToken.None);

        // Assert
        reopenSelf.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        reopenOther.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        deactivate.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        revoke.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        passkey.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        lockout.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        deputy.DeactivatedAt.Should().NotBeNull("the closed administrator stays closed");
        stranger.DeactivatedAt.Should().NotBeNull();
        subject.DeactivatedAt.Should().BeNull();
        (await tokens.ListAsync(subject.Id, CancellationToken.None)).Should().ContainSingle();
        (await users.GetPasskeysAsync(subject)).Should().ContainSingle();
        (await users.IsLockedOutAsync(subject)).Should().BeTrue();
        mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>
    /// An administrator id that names no row is refused the way a closed one is, rather than being
    /// taken on trust because nothing contradicts it.
    /// </summary>
    [Fact]
    public async Task AnAdministratorIdThatNamesNoAccountIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        // Act
        UserAdminResult result = await Administration(context, provider)
            .DeactivateAsync(9999, subject.Id, CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(UserAdminRefusal.ClosedAdministrator);
        subject.DeactivatedAt.Should().BeNull();
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

    [Fact]
    public async Task RevokingAPasskeyTakesThatOneLeavesTheRestAndSaysWhoDidIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, subject, "phone");
        UserPasskeyInfo laptop = await SeedPasskeyAsync(users, subject, "laptop");
        FakeLogger<UserAdministration> logger = new();

        // Act
        UserAdminResult result = await Administration(context, provider, logger)
            .RevokePasskeyAsync(admin.Id, subject.Id, phone.CredentialId, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Affected.Should().Be(1, "the count is what tells the page it was revoked rather than already gone");
        (await users.GetPasskeysAsync(subject)).Should().ContainSingle().Which.CredentialId.Should().Equal(laptop.CredentialId);

        FakeLogRecord record = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Warning);
        record.StructuredState.Should().Contain(property => property.Key == "AdministratorId" && property.Value == admin.Id.ToString(CultureInfo.InvariantCulture));
        record.StructuredState.Should().Contain(property => property.Key == "UserId" && property.Value == subject.Id.ToString(CultureInfo.InvariantCulture));
        record.StructuredState.Should().Contain(property => property.Key == "PasskeyName" && property.Value == "phone");
    }

    /// <summary>The owner is told, since the administrator is not the only person who can reach this screen with a session.</summary>
    [Fact]
    public async Task RevokingAPasskeyMailsItsOwner()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, subject, "phone");
        CapturingEmailSender mail = new();

        // Act
        await Administration(context, provider, mail: mail)
            .RevokePasskeyAsync(admin.Id, subject.Id, phone.CredentialId, CancellationToken.None);

        // Assert
        (string email, string subject, string body) sent = mail.SentEmails.Should().ContainSingle().Subject;
        sent.email.Should().Be("subject@example.com");
        sent.subject.Should().Be("An administrator removed a passkey from your Homespool account");
        sent.body.Should().NotContain("phone", "the name is the person's own, and the mail does not repeat it");
    }

    /// <summary>
    /// A credential id names one passkey anywhere, so the account in the request is what stops an
    /// administrator on one account's page revoking a passkey that belongs to another.
    /// </summary>
    [Fact]
    public async Task RevokingAnotherAccountsPasskeyThroughThisAccountRevokesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        HSUser bystander = await AddUserAsync(users, "bystander@example.com");
        UserPasskeyInfo theirs = await SeedPasskeyAsync(users, bystander, "theirs");
        FakeLogger<UserAdministration> logger = new();
        CapturingEmailSender mail = new();

        // Act
        UserAdminResult result = await Administration(context, provider, logger, mail)
            .RevokePasskeyAsync(admin.Id, subject.Id, theirs.CredentialId, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue("already gone is not a refusal");
        result.Affected.Should().Be(0);
        (await users.GetPasskeysAsync(bystander)).Should().ContainSingle();
        logger.Collector.GetSnapshot().Should().BeEmpty("nothing was revoked");
        mail.SentEmails.Should().BeEmpty("and nobody's passkey changed");
    }

    [Fact]
    public async Task AnAdministratorCannotRevokeTheirOwnPasskey()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, admin, "phone");
        CapturingEmailSender mail = new();

        // Act
        UserAdminResult result = await Administration(context, provider, mail: mail)
            .RevokePasskeyAsync(admin.Id, admin.Id, phone.CredentialId, CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(UserAdminRefusal.Self);
        (await users.GetPasskeysAsync(admin)).Should().ContainSingle();
        mail.SentEmails.Should().BeEmpty();
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
            await limiter.RecordFailedAttemptAsync(subject.Id, LimitedAction.ClaimPrinter, now, CancellationToken.None);
            await users.AccessFailedAsync(subject);
        }

        (await users.IsLockedOutAsync(subject)).Should().BeTrue("the fixture has to reach the state being cleared");
        (await limiter.RemainingLockoutAsync(subject.Id, LimitedAction.ClaimPrinter, now, CancellationToken.None))
            .Should().NotBeNull();

        // Act
        UserAdminResult result = await Administration(context, provider)
            .ClearLockoutAsync(admin.Id, subject.Id, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Affected.Should().Be(1, "one backoff row was standing");
        (await users.IsLockedOutAsync(subject)).Should().BeFalse();
        subject.AccessFailedCount.Should().Be(0);
        (await limiter.RemainingLockoutAsync(subject.Id, LimitedAction.ClaimPrinter, now, CancellationToken.None))
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
        UserAdminResult passkey = await administration.RevokePasskeyAsync(admin.Id, 9999, [1, 2, 3], CancellationToken.None);

        // Assert
        deactivate.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
        revoke.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
        lockout.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
        passkey.Refusal.Should().Be(UserAdminRefusal.NoSuchAccount);
    }

    private static UserAdministration Administration(HomespoolDbContext context,
                                                     IServiceProvider provider,
                                                     ILogger<UserAdministration>? logger = null,
                                                     CapturingEmailSender? mail = null)
    {
        return new UserAdministration(context,
                                      new ApiTokenService(context),
                                      provider.GetRequiredService<AttemptLimiter>(),
                                      new UnitOfWork(context),
                                      TimeProvider.System,
                                      (mail ?? new CapturingEmailSender()).Notices(),
                                      logger ?? NullLogger<UserAdministration>.Instance);
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

    private static async Task<UserPasskeyInfo> SeedPasskeyAsync(UserManager<HSUser> users, HSUser user, string name)
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
            clientDataJson: [])
        {
            Name = name,
        };

        (await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();

        return passkey;
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
