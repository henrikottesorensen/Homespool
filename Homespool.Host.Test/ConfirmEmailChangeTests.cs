using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Pages.Account;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Confirming an email change moves the address, and moves nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>ConfirmEmailChangeAtomicityTests</c>.</b> The page used to set the username alongside the
/// address, because the two were the same value and sign-in used the username - two round trips
/// through <c>UserManager</c> that could half-land, which is what the transaction was for. The
/// username is now the person's own and an address change does not touch it, so there is one round
/// trip and nothing to keep in step. What is left to prove is that the surviving call still lands, and
/// that the clash it can still hit changes nothing.
/// </para>
/// <para>
/// The clash is now caught a layer earlier: <c>RequireUniqueEmail</c> is on (sign-in resolves an
/// address to an account), so <c>ChangeEmailAsync</c>'s own validation refuses a second account on one
/// address. That is a rejected save rather than a rolled-back one, which is why no transaction
/// replaces the deleted one.
/// </para>
/// </remarks>
public sealed class ConfirmEmailChangeTests : IDisposable
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

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string userName, string email)
    {
        HSUser user = new(userName)
        {
            Email = email,
            EmailConfirmed = true,
        };

        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue();

        return user;
    }

    private static ConfirmEmailChangeModel NewModel(UserManager<HSUser> users,
                                                    SignInManager<HSUser> signIn,
                                                    DefaultHttpContext httpContext)
    {
        return new ConfirmEmailChangeModel(users, signIn, Options.Create(new SmtpOptions()))
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };
    }

    private static async Task<string> ChangeEmailCodeAsync(UserManager<HSUser> users, HSUser user, string newEmail)
    {
        string code = await users.GenerateChangeEmailTokenAsync(user, newEmail);

        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
    }

    /// <summary>The ordinary case: the address moves and the username is left alone.</summary>
    [Fact]
    public async Task ConfirmingAChangeMovesTheEmailAndLeavesTheUsernameAlone()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await AddUserAsync(users, "henrik", "before@example.com");
        string code = await ChangeEmailCodeAsync(users, user, "after@example.com");

        ConfirmEmailChangeModel model = NewModel(users, signIn, httpContext);

        // Act
        await model.OnGetAsync(user.Id.ToString(), "after@example.com", code);

        // Assert
        HSUser reloaded = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

        reloaded.Email.Should().Be("after@example.com");
        reloaded.UserName.Should().Be("henrik", "an address change is not a rename");
        model.StatusMessage.Should().StartWith("Thank you");
    }

    /// <summary>
    /// <b>The clash case.</b> Another account already holds the target address, so the change is
    /// refused and the row keeps every value it had.
    /// </summary>
    /// <remarks>
    /// Turn <c>RequireUniqueEmail</c> off and this test fails by letting the change through - two
    /// accounts on one address, which is precisely what would make <c>LoginModel</c>'s
    /// <c>FindByEmailAsync</c> pick one of them arbitrarily.
    /// </remarks>
    [Fact]
    public async Task AnAddressAnotherAccountHoldsLeavesTheAccountUnchanged()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await AddUserAsync(users, "taken", "taken@example.com");
        HSUser mover = await AddUserAsync(users, "mover", "mover@example.com");

        string code = await ChangeEmailCodeAsync(users, mover, "taken@example.com");

        ConfirmEmailChangeModel model = NewModel(users, signIn, httpContext);

        // Act
        IActionResult result = await model.OnGetAsync(mover.Id.ToString(), "taken@example.com", code);

        // Assert
        result.Should().BeOfType<PageResult>();

        // AsNoTracking, and a fresh read: the tracked instance still carries the values the refused
        // call set on it in memory, so asserting against it would pass whether or not anything was
        // written. What matters is the row.
        HSUser reloaded = await context.Users.AsNoTracking().SingleAsync(u => u.Id == mover.Id, TestContext.Current.CancellationToken);

        reloaded.Email.Should().Be("mover@example.com", "a refused change must leave the address where it was");
        reloaded.UserName.Should().Be("mover");

        model.StatusMessage.Should().Be("Error changing email.");
    }
}
