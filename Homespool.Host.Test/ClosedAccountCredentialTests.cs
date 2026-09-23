using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What a closed account can no longer do without signing in: redeem an emailed link, be sent one,
/// or be written back open by a save that read it before it closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of these presents a credential</b>, so the sign-in gate that refuses a closed account
/// everywhere else never sees them. A reset redeemed while closed leaves a password the account keeps
/// when it is reopened - a credential nobody chose to restore.
/// </para>
/// <para>
/// <b>The stale save is the subtle one.</b> The framework's save writes every column of the row as it
/// was loaded and checks only the concurrency stamp, so a request that read the account before an
/// administrator closed it could write it back open unless closing moves that stamp too.
/// </para>
/// </remarks>
public sealed class ClosedAccountCredentialTests : IDisposable
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string NewPassword = "Different-Horse-Battery-Staple-2!"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-closedcred-{Guid.NewGuid():N}.db");

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
    /// A reset link minted while the account is closed does not reset it, and the answer is the one a
    /// wrong code gets - so the form says nothing about the account being closed.
    /// </summary>
    [Fact]
    public async Task AResetLinkMintedWhileClosedIsRefusedAsAWrongCodeIs()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, IServiceProvider provider) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser admin = await AddUserAsync(users, "admin@example.com");
        await IdentityTestHarness.MakeAdministratorAsync(provider, users, admin);
        HSUser subject = await AddUserAsync(users, "closed@example.com");

        (await Administration(context, provider).DeactivateAsync(admin.Id, subject.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue("closing the account is setup");
        await context.Entry(subject).ReloadAsync(TestContext.Current.CancellationToken);

        string minted = await users.GeneratePasswordResetTokenAsync(subject);

        // Act
        (IActionResult closed, ModelStateDictionary closedState) =
            await PostResetAsync(context, users, httpContext, "closed@example.com", minted);
        (_, ModelStateDictionary wrongState) =
            await PostResetAsync(context, users, httpContext, "admin@example.com", "not-a-token");

        // Assert
        closed.Should().BeOfType<PageResult>("the reset was refused");
        Errors(closedState).Should().Equal(Errors(wrongState), "a closed account is answered as a wrong code is");

        HSUser stored = await ReadAsync(context, subject.Id);
        (await users.CheckPasswordAsync(stored, OldPassword)).Should().BeTrue("the password did not change");
        stored.DeactivatedAt.Should().NotBeNull();
    }

    /// <summary>
    /// A link minted before the account closed is dead after it, and stays dead once the account is
    /// reopened: closing moved the security stamp the link carries. This is the framework's behaviour,
    /// pinned because reopening deliberately leaves that stamp alone and relies on it.
    /// </summary>
    [Fact]
    public async Task ALinkMintedBeforeClosingStaysDeadAfterReopening()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser admin = await AddUserAsync(users, "admin@example.com");
        await IdentityTestHarness.MakeAdministratorAsync(provider, users, admin);
        HSUser subject = await AddUserAsync(users, "reopened@example.com");

        string minted = await users.GeneratePasswordResetTokenAsync(subject);

        UserAdministration administration = Administration(context, provider);
        (await administration.DeactivateAsync(admin.Id, subject.Id, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await administration.ReactivateAsync(admin.Id, subject.Id, CancellationToken.None)).Succeeded.Should().BeTrue();

        HSUser reopened = await ReadAsync(context, subject.Id);

        // Act
        IdentityResult result = await users.ResetPasswordAsync(reopened, minted, NewPassword);

        // Assert
        result.Succeeded.Should().BeFalse("the link carries the stamp closing replaced");
        result.Errors.Select(error => error.Code).Should().Contain(nameof(IdentityErrorDescriber.InvalidToken));
    }

    /// <summary>
    /// The other two emailed tokens are refused the same way: confirming an address and moving one.
    /// Each is redeemed on a page nobody has to be signed in to reach.
    /// </summary>
    [Fact]
    public async Task AClosedAccountCanNeitherConfirmNorChangeItsAddress()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser subject = await AddUserAsync(users, "closed@example.com", confirmed: false);

        string confirm = await users.GenerateEmailConfirmationTokenAsync(subject);
        string change = await users.GenerateChangeEmailTokenAsync(subject, "elsewhere@example.com");

        // Closed as UserAdministration closes one, minus the stamp, so that only the closure can be
        // what refuses the two tokens.
        subject.DeactivatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IdentityResult confirmed = await users.ConfirmEmailAsync(subject, confirm);
        IdentityResult changed = await users.ChangeEmailAsync(subject, "elsewhere@example.com", change);

        // Assert
        confirmed.Succeeded.Should().BeFalse();
        changed.Succeeded.Should().BeFalse();

        HSUser stored = await ReadAsync(context, subject.Id);
        stored.EmailConfirmed.Should().BeFalse();
        stored.Email.Should().Be("closed@example.com");
    }

    /// <summary>
    /// Both anonymous mail forms send a closed account nothing, and answer it as they answer an
    /// address nobody holds.
    /// </summary>
    [Fact]
    public async Task NeitherMailFormWritesToAClosedAccount()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser confirmed = await AddUserAsync(users, "closed@example.com");
        HSUser unconfirmed = await AddUserAsync(users, "pending@example.com", confirmed: false);

        foreach (HSUser user in new[] { confirmed, unconfirmed })
        {
            user.DeactivatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CapturingEmailSender sender = new();

        // Act
        IActionResult forgot = await ForgotModel(context, users, httpContext, sender, "closed@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        ResendEmailConfirmationModel closedResend = ResendModel(context, users, httpContext, sender, "pending@example.com");
        await closedResend.OnPostAsync(TestContext.Current.CancellationToken);

        ResendEmailConfirmationModel unknownResend = ResendModel(context, users, httpContext, sender, "nobody@example.com");
        await unknownResend.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        sender.SentEmails.Should().BeEmpty("a closed account is sent no link it could not use");
        forgot.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("./ForgotPasswordConfirmation");
        Errors(closedResend.ModelState).Should().Equal(Errors(unknownResend.ModelState), "answered as an unknown address is");
    }

    /// <summary>
    /// A reset that read the account before an administrator closed it, and saves after, fails -
    /// rather than writing the row back as it loaded it, open and with its old security stamp. The
    /// link was good when it was checked; the save is what has to notice.
    /// </summary>
    [Fact]
    public async Task ASaveThatReadTheAccountBeforeItClosedCannotReopenIt()
    {
        // Arrange - two requests, each with its own context, as two requests have
        await using HomespoolDbContext resetting = await MigratedContextAsync();
        await using HomespoolDbContext closing = NewContext();
        (UserManager<HSUser> resetUsers, _, _, IServiceProvider resetProvider) = IdentityTestHarness.BuildIdentityServices(resetting);
        (_, _, _, IServiceProvider closeProvider) = IdentityTestHarness.BuildIdentityServices(closing);

        HSUser admin = await AddUserAsync(resetUsers, "admin@example.com");
        await IdentityTestHarness.MakeAdministratorAsync(resetProvider, resetUsers, admin);
        HSUser subject = await AddUserAsync(resetUsers, "raced@example.com");

        // The reset has read the account and holds a good link for it.
        string minted = await resetUsers.GeneratePasswordResetTokenAsync(subject);

        (await Administration(closing, closeProvider).DeactivateAsync(admin.Id, subject.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        // Act - the reset saves the row it loaded, which still says open.
        IdentityResult result = await resetUsers.ResetPasswordAsync(subject, minted, NewPassword);

        // Assert
        result.Succeeded.Should().BeFalse("the row moved underneath it");
        result.Errors.Select(error => error.Code).Should().Contain(nameof(IdentityErrorDescriber.ConcurrencyFailure));

        await using HomespoolDbContext fresh = NewContext();
        HSUser stored = await fresh.Users.AsNoTracking().SingleAsync(u => u.Id == subject.Id, TestContext.Current.CancellationToken);
        stored.DeactivatedAt.Should().NotBeNull("the account stays closed");
        new PasswordHasher<HSUser>().VerifyHashedPassword(stored, stored.PasswordHash!, OldPassword)
            .Should().NotBe(PasswordVerificationResult.Failed, "and keeps the password it had");
    }

    /// <summary>
    /// The same in the other direction: a save of the row as it was loaded while closed fails once the
    /// account is reopened, rather than closing it again with nobody having asked.
    /// </summary>
    [Fact]
    public async Task ASaveThatReadTheAccountWhileClosedCannotCloseItAgain()
    {
        // Arrange
        await using HomespoolDbContext stale = await MigratedContextAsync();
        await using HomespoolDbContext reopening = NewContext();
        (UserManager<HSUser> staleUsers, _, _, IServiceProvider staleProvider) = IdentityTestHarness.BuildIdentityServices(stale);
        (_, _, _, IServiceProvider reopenProvider) = IdentityTestHarness.BuildIdentityServices(reopening);

        HSUser admin = await AddUserAsync(staleUsers, "admin@example.com");
        await IdentityTestHarness.MakeAdministratorAsync(staleProvider, staleUsers, admin);
        HSUser subject = await AddUserAsync(staleUsers, "reopened@example.com");

        (await Administration(stale, staleProvider).DeactivateAsync(admin.Id, subject.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        await stale.Entry(subject).ReloadAsync(TestContext.Current.CancellationToken);

        (await Administration(reopening, reopenProvider).ReactivateAsync(admin.Id, subject.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        // Act - a save of the row as loaded while it was closed
        subject.Language = "da";
        IdentityResult result = await staleUsers.UpdateAsync(subject);

        // Assert
        result.Succeeded.Should().BeFalse("the row moved underneath it");

        await using HomespoolDbContext fresh = NewContext();
        (await fresh.Users.AsNoTracking().SingleAsync(u => u.Id == subject.Id, TestContext.Current.CancellationToken))
            .DeactivatedAt.Should().BeNull("the account stays open");
    }

    private static async Task<(IActionResult result, ModelStateDictionary state)> PostResetAsync(
        HomespoolDbContext context,
        UserManager<HSUser> users,
        DefaultHttpContext httpContext,
        string email,
        string code)
    {
        ResetPasswordModel model = new(users,
                                       new ApiTokenService(context),
                                       new UnitOfWork(context),
                                       TestLocaliser.Shared(),
                                       NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ResetPasswordModel.InputModel
            {
                Email = email,
                Password = NewPassword,
                ConfirmPassword = NewPassword,
                Code = code,
            },
        };

        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        return (result, model.ModelState);
    }

    private static ForgotPasswordModel ForgotModel(HomespoolDbContext context,
                                                   UserManager<HSUser> users,
                                                   DefaultHttpContext httpContext,
                                                   CapturingEmailSender sender,
                                                   string email)
    {
        return new ForgotPasswordModel(users, sender, Limiter(context), TimeProvider.System, TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ForgotPasswordModel.InputModel { Email = email },
        };
    }

    private static ResendEmailConfirmationModel ResendModel(HomespoolDbContext context,
                                                            UserManager<HSUser> users,
                                                            DefaultHttpContext httpContext,
                                                            CapturingEmailSender sender,
                                                            string email)
    {
        return new ResendEmailConfirmationModel(users, sender, Limiter(context), TimeProvider.System, TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ResendEmailConfirmationModel.InputModel { Email = email },
        };
    }

    private static AttemptLimiter Limiter(HomespoolDbContext context)
    {
        return new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                  NullLogger<AttemptLimiter>.Instance);
    }

    private static UserAdministration Administration(HomespoolDbContext context, IServiceProvider provider)
    {
        return new UserAdministration(context,
                                      new ApiTokenService(context),
                                      provider.GetRequiredService<UserSessionService>(),
                                      provider.GetRequiredService<AttemptLimiter>(),
                                      new UnitOfWork(context),
                                      TimeProvider.System,
                                      new CapturingEmailSender().Notices(),
                                      NullLogger<UserAdministration>.Instance);
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email, bool confirmed = true)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = confirmed,
        };

        IdentityResult created = await users.CreateAsync(user, OldPassword);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    private static async Task<HSUser> ReadAsync(HomespoolDbContext context, long userId)
    {
        HSUser user = await context.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        await context.Entry(user).ReloadAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private static IEnumerable<string> Errors(ModelStateDictionary state)
    {
        return state.Values.SelectMany(entry => entry.Errors).Select(error => error.ErrorMessage).ToList();
    }

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
}
