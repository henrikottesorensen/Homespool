using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Which password path takes an account's personal access tokens with it: the emailed reset link
/// does, and the signed-in change page deliberately does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The asymmetry is the subject of this file.</b> Reaching the change page takes the current
/// password inside a live session, so it is somebody rotating a credential they hold, and breaking
/// every script they run is a poor answer to routine hygiene. The reset link is where somebody
/// locked out of a compromised account arrives, and there a token the attacker minted is exactly
/// what has to go.
/// </para>
/// <para>
/// <b>The failure cases carry the weight on the reset path.</b> An implementation that revoked
/// before verifying the code would pass a happy-path test while letting anyone who can reach the
/// form - which is anyone - destroy the tokens of any address they can guess. The transaction is
/// what makes that impossible rather than merely unlikely.
/// </para>
/// </remarks>
public sealed class PasswordTokenRevocationTests : IDisposable
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!";
    private const string NewPassword = "Different-Horse-Battery-Staple-2!";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pwrevoke-{Guid.NewGuid():N}.db");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

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

    private static async Task<HSUser> AddUserWithPasswordAsync(UserManager<HSUser> users, string email)
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

    // ---------- the signed-in change page ----------

    /// <summary>
    /// A rotation from inside a session leaves the account's tokens working, and says nothing about
    /// them - there is nothing to report, and a message about tokens would suggest otherwise.
    /// </summary>
    [Fact]
    public async Task ChangingAPasswordLeavesTheAccountsTokensAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, LocalSignIn signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "changer@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        (_, string first) = await tokens.CreateAsync(user.Id, "laptop", CapabilitySet.Everything, CancellationToken.None);
        await tokens.CreateAsync(user.Id, "ci", CapabilitySet.Everything, CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, TestLocaliser.Shared(),
                                        NullLogger<ChangePasswordModel>.Instance)
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
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await users.CheckPasswordAsync(user, NewPassword)).Should().BeTrue("the password did change");

        (await context.ApiTokens.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await tokens.FindByCredentialAsync(first, CancellationToken.None)).Should().NotBeNull(
            "a token that no longer authenticates has been revoked, whatever the row count says");

        model.StatusMessage.Should().Be("Your password has been changed.");
    }

    /// <summary>
    /// A wrong current password changes nothing - not the password, and not anything else on the
    /// account either.
    /// </summary>
    [Fact]
    public async Task AFailedPasswordChangeChangesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, LocalSignIn signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "wrongpass@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CapabilitySet.Everything, CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, TestLocaliser.Shared(),
                                        NullLogger<ChangePasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ChangePasswordModel.InputModel
            {
                OldPassword = "not-the-current-password",
                NewPassword = NewPassword,
                ConfirmPassword = NewPassword,
            },
        };

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>("the change was rejected");
        (await tokens.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().NotBeNull();

        (await users.CheckPasswordAsync(user, OldPassword)).Should().BeTrue("the password must not have changed either");
    }

    // ---------- the emailed reset link ----------

    /// <summary>
    /// The path that matters most: recovering a compromised account by email link revokes whatever
    /// the attacker minted while they held it.
    /// </summary>
    [Fact]
    public async Task ResettingAPasswordRevokesTheAccountsTokens()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "resetter@example.com");

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "attacker's", CapabilitySet.Everything, CancellationToken.None);

        ResetPasswordModel model = new(users, tokens, new UnitOfWork(context),
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
        (await tokens.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().BeNull();
        (await users.CheckPasswordAsync(user, NewPassword)).Should().BeTrue();
    }

    /// <summary>
    /// <b>An invalid reset code must not revoke anything.</b> The reset form is reachable by anyone -
    /// that is the point of it - so revoking before the code is verified would let a stranger disable
    /// any account's tokens by guessing at an email address.
    /// </summary>
    [Fact]
    public async Task AFailedResetLeavesTheTokensAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "badcode@example.com");

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CapabilitySet.Everything, CancellationToken.None);

        ResetPasswordModel model = new(users, tokens, new UnitOfWork(context),
                                       new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                                          NullLogger<AttemptLimiter>.Instance),
                                       TestLocaliser.Shared(),
                                       NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ResetPasswordModel.InputModel
            {
                Email = "badcode@example.com",
                Password = NewPassword,
                ConfirmPassword = NewPassword,
                Code = "not-a-real-reset-code",
            },
        };

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>("the reset was rejected");
        (await tokens.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().NotBeNull();
        (await users.CheckPasswordAsync(user, OldPassword)).Should().BeTrue();
    }
}
