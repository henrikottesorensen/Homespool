using System;
using System.Buffers.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Pages.Admin;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The administrator's passkey screen: it lists every account's credentials, and revoking one
/// removes exactly that one.
/// </summary>
public sealed class AdminPasskeysPageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-adminpasskeys-{Guid.NewGuid():N}.db");

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
    public async Task ListsEveryAccountsPasskeysByOwner()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext request, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser alice = await AddUserAsync(users, "alice@example.com");
        HSUser bob = await AddUserAsync(users, "bob@example.com");
        await SeedPasskeyAsync(users, bob, "phone");
        await SeedPasskeyAsync(users, alice, "laptop");
        PasskeysModel model = NewModel(context, users, request, alice);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Rows.Should().HaveCount(2);
        model.Rows.Select(r => r.UserName).Should().ContainInOrder("alice", "bob");
        model.Rows.Select(r => r.Name).Should().BeEquivalentTo("laptop", "phone");
    }

    [Fact]
    public async Task RevokingRemovesThatPasskeyAndNoOther()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext request, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser bob = await AddUserAsync(users, "bob@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, bob, "phone");
        UserPasskeyInfo laptop = await SeedPasskeyAsync(users, bob, "laptop");
        PasskeysModel model = NewModel(context, users, request, admin);

        // Act
        IActionResult result = await model.OnPostRevokeAsync(bob.Id, Base64Url.EncodeToString(phone.CredentialId));

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("Passkey revoked.");
        (await users.GetPasskeysAsync(bob)).Should().ContainSingle().Which.CredentialId.Should().Equal(laptop.CredentialId);
    }

    [Fact]
    public async Task RevokingAnUnknownPasskeyReportsGone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext request, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        PasskeysModel model = NewModel(context, users, request, admin);

        // Act
        IActionResult result = await model.OnPostRevokeAsync(admin.Id, Base64Url.EncodeToString(Guid.NewGuid().ToByteArray()));
        IActionResult malformed = await model.OnPostRevokeAsync(admin.Id, "not base64url!");

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("That passkey was already gone.");
        malformed.Should().BeOfType<NotFoundResult>();
    }

    private static PasskeysModel NewModel(HomespoolDbContext context, UserManager<HSUser> users, DefaultHttpContext request, HSUser admin)
    {
        IdentityTestHarness.SignInAsPrincipal(request, admin);

        return new PasskeysModel(context, users, TestLocaliser.Shared(), NullLogger<PasskeysModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
        };
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
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
