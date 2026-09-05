using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The passkey table: that the model maps it at all, and that a credential record survives the trip
/// through the store and back.
/// </summary>
/// <remarks>
/// <b>The first test is the one that matters.</b> The framework maps the passkey entity only under a
/// schema-version option it reads from the application's service provider while building the model,
/// so a context built any other way silently comes out without the table. <c>HomespoolDbContext</c>
/// maps it explicitly instead, and this test is what says so for a context built with nothing but
/// connection options - which is how every test and the design-time tool build one.
/// </remarks>
public sealed class PasskeyStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-passkey-{Guid.NewGuid():N}.db");

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
    public async Task TheTableIsMappedForAContextBuiltWithoutAServiceProvider()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        IEntityType? entity = context.Model.FindEntityType(typeof(IdentityUserPasskey<long>));
        List<string> tables = await context.Database
                                           .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
                                           .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        entity.Should().NotBeNull("the context maps the passkey entity itself rather than leaving it to a schema-version option");
        entity!.GetTableName().Should().Be("AspNetUserPasskeys");
        tables.Should().Contain("AspNetUserPasskeys", "the migration must carry what the model maps");
    }

    [Fact]
    public async Task ACredentialRecordRoundTripsThroughTheStore()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser user = await AddUserAsync(users);

        DateTimeOffset created = new(2026, 9, 5, 12, 30, 0, TimeSpan.Zero);
        UserPasskeyInfo passkey = NewPasskey(created);

        // Act
        IdentityResult added = await users.AddOrUpdatePasskeyAsync(user, passkey);
        IList<UserPasskeyInfo> listed = await users.GetPasskeysAsync(user);
        UserPasskeyInfo? found = await users.GetPasskeyAsync(user, passkey.CredentialId);
        HSUser? owner = await users.FindByPasskeyIdAsync(passkey.CredentialId);

        // Assert
        added.Succeeded.Should().BeTrue();
        listed.Should().ContainSingle();
        found.Should().NotBeNull();
        found!.CredentialId.Should().Equal(passkey.CredentialId);
        found.PublicKey.Should().Equal(passkey.PublicKey);
        found.Name.Should().Be("MacBook");
        found.CreatedAt.Should().Be(created, "the timestamp lives inside the JSON column and must survive the convention that stores timestamps as integers");
        found.SignCount.Should().Be(7);
        found.Transports.Should().Equal("internal", "hybrid");
        found.IsUserVerified.Should().BeTrue();
        found.IsBackupEligible.Should().BeTrue();
        found.IsBackedUp.Should().BeFalse();
        found.AttestationObject.Should().Equal(passkey.AttestationObject);
        found.ClientDataJson.Should().Equal(passkey.ClientDataJson);
        owner!.Id.Should().Be(user.Id);
    }

    /// <summary>
    /// The engine hands back the same credential with its counter moved on after every assertion,
    /// and storing it must update the row rather than add a second one under the same id.
    /// </summary>
    [Fact]
    public async Task StoringTheSameCredentialAgainUpdatesTheRow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser user = await AddUserAsync(users);
        UserPasskeyInfo passkey = NewPasskey(DateTimeOffset.UtcNow);

        await users.AddOrUpdatePasskeyAsync(user, passkey);

        passkey.SignCount = 8;
        passkey.IsBackedUp = true;
        passkey.Name = "MacBook (renamed)";

        // Act
        IdentityResult updated = await users.AddOrUpdatePasskeyAsync(user, passkey);
        IList<UserPasskeyInfo> listed = await users.GetPasskeysAsync(user);

        // Assert
        updated.Succeeded.Should().BeTrue();
        listed.Should().ContainSingle();
        listed[0].SignCount.Should().Be(8);
        listed[0].IsBackedUp.Should().BeTrue();
        listed[0].Name.Should().Be("MacBook (renamed)");
    }

    [Fact]
    public async Task RemovingACredentialLeavesTheOthers()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser user = await AddUserAsync(users);
        UserPasskeyInfo first = NewPasskey(DateTimeOffset.UtcNow);
        UserPasskeyInfo second = NewPasskey(DateTimeOffset.UtcNow);

        await users.AddOrUpdatePasskeyAsync(user, first);
        await users.AddOrUpdatePasskeyAsync(user, second);

        // Act
        IdentityResult removed = await users.RemovePasskeyAsync(user, first.CredentialId);
        IList<UserPasskeyInfo> listed = await users.GetPasskeysAsync(user);

        // Assert
        removed.Succeeded.Should().BeTrue();
        listed.Should().ContainSingle().Which.CredentialId.Should().Equal(second.CredentialId);
    }

    /// <summary>
    /// A credential id is the key, so one credential belongs to one account: filing it under a second
    /// account moves it rather than duplicating it, which the engine's own registration check refuses
    /// before this point. Pinned here so the table's shape does not drift under that check.
    /// </summary>
    [Fact]
    public async Task ACredentialIdIsUniqueAcrossAccounts()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser first = await AddUserAsync(users, "first@example.com");
        HSUser second = await AddUserAsync(users, "second@example.com");
        UserPasskeyInfo passkey = NewPasskey(DateTimeOffset.UtcNow);

        await users.AddOrUpdatePasskeyAsync(first, passkey);

        // Act
        await users.AddOrUpdatePasskeyAsync(second, passkey);
        int rows = await context.Set<IdentityUserPasskey<long>>().CountAsync(TestContext.Current.CancellationToken);

        // Assert
        rows.Should().Be(1, "the credential id is the primary key");
    }

    /// <summary>
    /// The cascade from the account, as <c>ApiToken</c> has it: a credential pointing at an id that
    /// no longer resolves guards nothing.
    /// </summary>
    [Fact]
    public async Task DeletingTheAccountTakesItsPasskeys()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser user = await AddUserAsync(users);

        await users.AddOrUpdatePasskeyAsync(user, NewPasskey(DateTimeOffset.UtcNow));

        // Tracked, so the context's own cascade runs; the database's would need the pragma the
        // application's interceptor sets per connection, which a bare test context does not have.
        await context.Set<IdentityUserPasskey<long>>().LoadAsync(TestContext.Current.CancellationToken);

        // Act
        context.Users.Remove(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        int rows = await context.Set<IdentityUserPasskey<long>>().CountAsync(TestContext.Current.CancellationToken);

        // Assert
        rows.Should().Be(0);
    }

    private static UserPasskeyInfo NewPasskey(DateTimeOffset createdAt)
    {
        return new UserPasskeyInfo(
            credentialId: Guid.NewGuid().ToByteArray(),
            publicKey: [1, 2, 3, 4],
            createdAt: createdAt,
            signCount: 7,
            transports: ["internal", "hybrid"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: false,
            attestationObject: [9, 8, 7],
            clientDataJson: [6, 5, 4])
        {
            Name = "MacBook",
        };
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email = "owner@example.com")
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, "Correct horse battery staple 1");
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
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
