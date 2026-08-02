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
using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Changing a password revokes that account's personal access tokens, on both paths that can set
/// one: the signed-in change page and the emailed reset link.
/// </summary>
/// <remarks>
/// <para>
/// The decision and its cost are in <c>notes/api-tokens.md</c>. The short version: the state worth
/// making unreachable is "new password, old tokens still live", because that is precisely what
/// someone changing their password after a compromise believes they have escaped.
/// </para>
/// <para>
/// <b>The failure cases carry the weight here.</b> An implementation that revoked unconditionally -
/// before checking the old password, or on a rejected reset - would pass a naive happy-path test
/// while handing anyone who can reach the form a way to disable someone else's automation. Those are
/// the tests that were written red first.
/// </para>
/// </remarks>
public sealed class PasswordChangeRevokesTokensTests : IDisposable
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!";
    private const string NewPassword = "Different-Horse-Battery-Staple-2!";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pwrevoke-{Guid.NewGuid():N}.db");

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
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
        HSUser user = new(email)
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
    /// The happy path: the password changes, every token the account held is gone, and the status
    /// message says so rather than leaving the owner to discover it when a script starts failing.
    /// </summary>
    [Fact]
    public async Task ChangingAPasswordRevokesTheAccountsTokens()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "changer@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        (_, string first) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);
        await tokens.CreateAsync(user.Id, "ci", CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, tokens, new UnitOfWork(context), NullLogger<ChangePasswordModel>.Instance)
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

        (await context.ApiTokens.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await tokens.FindByCredentialAsync(first, CancellationToken.None)).Should().BeNull(
            "a token that still authenticates has not been revoked, whatever the row count says");

        model.StatusMessage.Should().Contain("2 API tokens were revoked");
    }

    /// <summary>
    /// <b>A wrong current password must not revoke anything.</b> Otherwise reaching the form - which
    /// only needs a session, not the password - is enough to destroy someone's tokens, and the
    /// transaction is what makes it impossible rather than merely unlikely.
    /// </summary>
    [Fact]
    public async Task AFailedPasswordChangeLeavesTheTokensAlone()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "wrongpass@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, tokens, new UnitOfWork(context), NullLogger<ChangePasswordModel>.Instance)
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
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>("the change was rejected");
        (await tokens.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().NotBeNull();

        (await users.CheckPasswordAsync(user, OldPassword)).Should().BeTrue("the password must not have changed either");
    }

    /// <summary>
    /// An account with no tokens gets the plain message. The count is only worth saying when there is
    /// something to say; most accounts hold none.
    /// </summary>
    [Fact]
    public async Task AnAccountWithNoTokensIsNotToldAboutThem()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "notokens@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ChangePasswordModel model = new(users, signIn, new ApiTokenService(context), new UnitOfWork(context),
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
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.StatusMessage.Should().Be("Your password has been changed.");
    }

    /// <summary>One token reads as "1 API token was revoked", not "1 API tokens".</summary>
    [Fact]
    public async Task ASingleTokenIsReportedInTheSingular()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "onetoken@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, tokens, new UnitOfWork(context), NullLogger<ChangePasswordModel>.Instance)
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
        model.StatusMessage.Should().Be("Your password has been changed. 1 API token was revoked.");
    }

    /// <summary>Only the account whose password changed loses its tokens.</summary>
    [Fact]
    public async Task AnotherAccountsTokensSurvive()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "mine@example.com");
        HSUser other = await AddUserWithPasswordAsync(users, "theirs@example.com");
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokenService tokens = new(context);
        await tokens.CreateAsync(user.Id, "mine", CancellationToken.None);
        (_, string theirs) = await tokens.CreateAsync(other.Id, "theirs", CancellationToken.None);

        ChangePasswordModel model = new(users, signIn, tokens, new UnitOfWork(context), NullLogger<ChangePasswordModel>.Instance)
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
        (await tokens.FindByCredentialAsync(theirs, CancellationToken.None)).Should().NotBeNull();
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
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "resetter@example.com");

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "attacker's", CancellationToken.None);

        ResetPasswordModel model = new(users, tokens, new UnitOfWork(context), NullLogger<ResetPasswordModel>.Instance)
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
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserWithPasswordAsync(users, "badcode@example.com");

        ApiTokenService tokens = new(context);
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        ResetPasswordModel model = new(users, tokens, new UnitOfWork(context), NullLogger<ResetPasswordModel>.Instance)
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
