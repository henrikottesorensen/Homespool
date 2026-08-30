using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The per-account bound on the two anonymous forms that send mail to a typed address.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is being bounded is a spend, not a guess.</b> Forgot-password and resend-confirmation
/// are anonymous and unauthenticated, so nothing else stands between "knows an address" and "fills
/// that inbox at request rate while draining the deployment's SMTP quota". The address is the only
/// stable handle the caller offers, so the target account is what the limiter keys on.
/// </para>
/// <para>
/// <b>The refusal must be invisible from outside.</b> Both forms already answer identically for
/// unknown and known addresses so as not to be enumeration oracles; a backed-off account has to get
/// that same answer, or the backoff itself becomes the existence signal.
/// </para>
/// </remarks>
public sealed class AccountEmailBackoffTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-email-backoff-{Guid.NewGuid():N}.db");

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

    /// <summary>Each reset mail is counted against the account it is addressed to.</summary>
    [Fact]
    public async Task AResetRequestIsCountedAgainstTheAccountItMails()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "counted@example.com");
        CapturingEmailSender sender = new();

        await NewForgotModel(context, users, httpContext, sender, "counted@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle();

        UserActionAttempt attempt = await context.UserActionAttempts.AsNoTracking()
            .SingleAsync(a => a.UserId == user.Id && a.Action == LimitedAction.SendPasswordResetEmail,
                         TestContext.Current.CancellationToken);

        attempt.FailedCount.Should().Be(1, "a send is what this limiter counts");
    }

    /// <summary>
    /// A backed-off account gets the identical redirect and no mail - the answer an unknown address
    /// gets, because a refusal that looked different would say the address is registered.
    /// </summary>
    [Fact]
    public async Task ABackedOffAccountIsAnsweredIdenticallyAndGetsNoMail()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "bombed@example.com");

        context.UserActionAttempts.Add(new UserActionAttempt
        {
            UserId = user.Id,
            Action = LimitedAction.SendPasswordResetEmail,
            FailedCount = 6,
            LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CapturingEmailSender sender = new();
        IActionResult result = await NewForgotModel(context, users, httpContext, sender, "bombed@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().BeEmpty("the backoff is the whole point");
        result.Should().BeOfType<RedirectToPageResult>()
              .Which.PageName.Should().Be("./ForgotPasswordConfirmation",
                                          "the refusal must be indistinguishable from a send");
    }

    /// <summary>
    /// Grinding at one address stops producing mail once the allowance is spent: with the default
    /// five free attempts, the sixth send arms the backoff and the seventh is refused.
    /// </summary>
    [Fact]
    public async Task GrindingAtAnAddressStopsProducingMail()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, "ground@example.com");
        CapturingEmailSender sender = new();

        for (int i = 0; i < 10; i++)
        {
            await NewForgotModel(context, users, httpContext, sender, "ground@example.com")
                .OnPostAsync(TestContext.Current.CancellationToken);
        }

        // The first backoff is 30 seconds and this loop takes far less, so the count is exact: five
        // free sends, a sixth that arms the lockout as it goes out, and nothing after it.
        sender.SentEmails.Should().HaveCount(6, "ten requests must not become ten emails");
    }

    /// <summary>An unknown address counts nothing, so the table cannot say what exists.</summary>
    [Fact]
    public async Task AnUnknownAddressWritesNothing()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        CapturingEmailSender sender = new();
        await NewForgotModel(context, users, httpContext, sender, "nobody@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().BeEmpty();
        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    /// <summary>
    /// A completed reset clears the count - the counted mail was acted on, so the backoff only ever
    /// stands between an address and mail nobody is using.
    /// </summary>
    [Fact]
    public async Task CompletingTheResetClearsTheBackoff()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "recovers@example.com");

        AttemptLimiter limiter = NewLimiter(context);
        for (int i = 0; i < 3; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.SendPasswordResetEmail,
                                                   DateTimeOffset.UtcNow, CancellationToken.None);
        }

        ResetPasswordModel model = new(users, new ApiTokenService(context), new UnitOfWork(context),
                                       NewLimiter(context), TestLocaliser.Shared(),
                                       NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ResetPasswordModel.InputModel
            {
                Email = "recovers@example.com",
                Password = "Different-Horse-Battery-Staple-2!",
                ConfirmPassword = "Different-Horse-Battery-Staple-2!",
                Code = await users.GeneratePasswordResetTokenAsync(user),
            },
        };

        await model.OnPostAsync(CancellationToken.None);

        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "a completed reset is what the counted emails were for");
    }

    /// <summary>The resend-confirmation form is bounded the same way, on its own counter.</summary>
    [Fact]
    public async Task AResendIsCountedAndABackedOffAccountGetsTheSameSentence()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "resend@example.com", confirmed: false);
        CapturingEmailSender sender = new();

        await NewResendModel(context, users, httpContext, sender, "resend@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle();

        // The send above already created the row, so the backoff is armed on it rather than added.
        UserActionAttempt attempt = await context.UserActionAttempts
            .SingleAsync(a => a.UserId == user.Id && a.Action == LimitedAction.SendConfirmationEmail,
                         TestContext.Current.CancellationToken);

        attempt.FailedCount = 6;
        attempt.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IActionResult result = await NewResendModel(context, users, httpContext, sender, "resend@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle("the backed-off resend must not mail");
        result.Should().BeOfType<PageResult>("the same page and the same sentence as a successful resend");
    }

    /// <summary>Confirming the address clears the resend counter.</summary>
    [Fact]
    public async Task ConfirmingTheAddressClearsTheBackoff()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "confirms@example.com", confirmed: false);

        await NewLimiter(context).RecordFailedAttemptAsync(user.Id, LimitedAction.SendConfirmationEmail,
                                                           DateTimeOffset.UtcNow, CancellationToken.None);

        string token = await users.GenerateEmailConfirmationTokenAsync(user);

        ConfirmEmailModel model = new(users, NewLimiter(context), TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        await model.OnGetAsync(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
                               TestContext.Current.CancellationToken);

        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    private static AttemptLimiter NewLimiter(HomespoolDbContext context)
    {
        return new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                  NullLogger<AttemptLimiter>.Instance);
    }

    private static ForgotPasswordModel NewForgotModel(HomespoolDbContext context,
                                                      UserManager<HSUser> users,
                                                      DefaultHttpContext httpContext,
                                                      CapturingEmailSender sender,
                                                      string email)
    {
        return new ForgotPasswordModel(users, sender, NewLimiter(context), TimeProvider.System,
                                       TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ForgotPasswordModel.InputModel { Email = email },
        };
    }

    private static ResendEmailConfirmationModel NewResendModel(HomespoolDbContext context,
                                                               UserManager<HSUser> users,
                                                               DefaultHttpContext httpContext,
                                                               CapturingEmailSender sender,
                                                               string email)
    {
        return new ResendEmailConfirmationModel(users, sender, NewLimiter(context), TimeProvider.System,
                                                TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ResendEmailConfirmationModel.InputModel { Email = email },
        };
    }

    private static async Task<HSUser> SeedUserAsync(UserManager<HSUser> users, string email, bool confirmed = true)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = confirmed,
        };

        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue(); // betterleaks:allow

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
