using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Data;
using Homespool.Host.Pages.Account;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// Confirming an email change moves the address and the sign-in name together, or moves neither.
/// </summary>
/// <remarks>
/// <para>
/// The two are separate round trips through <c>UserManager</c> - <c>ChangeEmailAsync</c> then
/// <c>SetUserNameAsync</c> - and in this project the username <em>is</em> the sign-in identifier
/// (<c>HSUser.DisplayName</c>'s remarks). Landing only the first leaves an account that displays the
/// new address and signs in under the old one.
/// </para>
/// <para>
/// <b>The failure is reachable, which is why this is tested rather than assumed:</b> another account
/// may already hold the target address as its username. <c>SetUserNameAsync</c> refuses that;
/// <c>ChangeEmailAsync</c> never checks it, because <c>RequireUniqueEmail</c> is off. So the second
/// call fails after the first has succeeded, which is exactly the split the transaction exists to
/// prevent.
/// </para>
/// </remarks>
public sealed class ConfirmEmailChangeAtomicityTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-emailchange-{Guid.NewGuid():N}.db");

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
        await context.Database.MigrateAsync();

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

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(email)
        {
            Email = email,
            EmailConfirmed = true,
        };

        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue();

        return user;
    }

    private static ConfirmEmailChangeModel NewModel(HSDbContext context,
                                                    UserManager<HSUser> users,
                                                    SignInManager<HSUser> signIn,
                                                    DefaultHttpContext httpContext)
    {
        return new ConfirmEmailChangeModel(users, signIn, new UnitOfWork(context),
            Options.Create(new SmtpOptions()))
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };
    }

    private static async Task<string> ChangeEmailCodeAsync(UserManager<HSUser> users, HSUser user, string newEmail)
    {
        string code = await users.GenerateChangeEmailTokenAsync(user, newEmail);

        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
    }

    /// <summary>The ordinary case: both the address and the sign-in name move.</summary>
    [Fact]
    public async Task ConfirmingAChangeMovesTheEmailAndTheUsernameTogether()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserAsync(users, "before@example.com");
        string code = await ChangeEmailCodeAsync(users, user, "after@example.com");

        ConfirmEmailChangeModel model = NewModel(context, users, signIn, httpContext);

        // Act
        await model.OnGetAsync(user.Id.ToString(), "after@example.com", code, CancellationToken.None);

        // Assert
        HSUser reloaded = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        reloaded.Email.Should().Be("after@example.com");
        reloaded.UserName.Should().Be("after@example.com", "the username is the sign-in identifier here");
        model.StatusMessage.Should().StartWith("Thank you");
    }

    /// <summary>
    /// <b>The rollback case.</b> When the target address is already somebody's username the second
    /// call fails, and the first must not survive it - the account keeps both of its old values.
    /// Remove the transaction and the email moves while the username does not.
    /// </summary>
    [Fact]
    public async Task AUsernameClashLeavesTheAccountCompletelyUnchanged()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await AddUserAsync(users, "taken@example.com");
        HSUser mover = await AddUserAsync(users, "mover@example.com");

        string code = await ChangeEmailCodeAsync(users, mover, "taken@example.com");

        ConfirmEmailChangeModel model = NewModel(context, users, signIn, httpContext);

        // Act
        IActionResult result = await model.OnGetAsync(mover.Id.ToString(), "taken@example.com", code, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();

        // AsNoTracking, and a fresh read: the tracked instance still carries the values the rolled-back
        // calls set on it in memory, so asserting against it would pass whether or not anything was
        // written. What matters is the row.
        HSUser reloaded = await context.Users.AsNoTracking().SingleAsync(u => u.Id == mover.Id);

        reloaded.Email.Should().Be("mover@example.com", "the email change must have rolled back with the username");
        reloaded.UserName.Should().Be("mover@example.com");

        model.StatusMessage.Should().Contain("not available");
    }
}
