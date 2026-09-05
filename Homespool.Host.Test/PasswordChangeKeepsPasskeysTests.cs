using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A password change or reset leaves the account's passkeys standing, unlike its API tokens, and the
/// change page says so to the person - who is the one who can tell a passkey they added from one
/// they did not.
/// </summary>
/// <remarks>
/// <b>Decided against the sweep the token precedent suggests</b> (Henrik: <i>"Why should a changed
/// password invalidate my face id passkey?"</i>). A passkey is the person's daily sign-in, and nothing
/// can add one without the password, so a hijacked session cannot have planted one; what a change
/// cannot rule out is a passkey added by somebody who knew the password, and for that the person is
/// told where to look rather than having every device re-enrolled.
/// </remarks>
public sealed class PasswordChangeKeepsPasskeysTests : IDisposable
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string NewPassword = "Different-Horse-Battery-Staple-2!"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pkkeep-{Guid.NewGuid():N}.db");

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
    public async Task ChangingAPasswordKeepsThePasskeysAndSaysSo()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserAsync(users, "changer@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);
        await SeedPasskeyAsync(users, user, "phone");
        await SeedPasskeyAsync(users, user, "laptop");

        ChangePasswordModel model = new(users, signIn, new ApiTokenService(context), new UnitOfWork(context),
                                        TestLocaliser.Shared(), NullLogger<ChangePasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ChangePasswordModel.InputModel
            {
                OldPassword = OldPassword,
                NewPassword = NewPassword,
                ConfirmPassword = NewPassword,
            },
        };

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await users.GetPasskeysAsync(user)).Should().HaveCount(2, "a passkey is the person's sign-in, not a token to sweep");
        model.StatusMessage.Should().Contain("2 passkey(s) still sign you in");
    }

    [Fact]
    public async Task AnAccountWithNoPasskeysIsNotToldAboutThem()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserAsync(users, "nokeys@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ChangePasswordModel model = new(users, signIn, new ApiTokenService(context), new UnitOfWork(context),
                                        TestLocaliser.Shared(), NullLogger<ChangePasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ChangePasswordModel.InputModel
            {
                OldPassword = OldPassword,
                NewPassword = NewPassword,
                ConfirmPassword = NewPassword,
            },
        };

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.StatusMessage.Should().Be("Your password has been changed.");
    }

    [Fact]
    public async Task ResettingAPasswordKeepsThePasskeys()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserAsync(users, "resetter@example.com");
        await SeedPasskeyAsync(users, user, "phone");

        ResetPasswordModel model = new(users, new ApiTokenService(context), new UnitOfWork(context),
                                       new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                                          NullLogger<AttemptLimiter>.Instance),
                                       TestLocaliser.Shared(),
                                       NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ResetPasswordModel.InputModel
            {
                Email = "resetter@example.com",
                Password = NewPassword,
                ConfirmPassword = NewPassword,
                Code = await users.GeneratePasswordResetTokenAsync(user),
            },
        };

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await users.GetPasskeysAsync(user)).Should().ContainSingle("the confirmation page tells the person to look, and the list is what they look at");
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, OldPassword);
        created.Succeeded.Should().BeTrue("account creation is setup for these tests, not what they verify");

        return user;
    }

    private static async Task SeedPasskeyAsync(UserManager<HSUser> users, HSUser user, string name)
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
