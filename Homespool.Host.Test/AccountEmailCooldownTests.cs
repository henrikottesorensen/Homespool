using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The per-account cooldown on the two anonymous forms that send mail to a typed address.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is being bounded is a spend, not a guess.</b> Forgot-password and resend-confirmation
/// are anonymous and unauthenticated, so nothing else stands between "knows an address" and "fills
/// that inbox at request rate while draining the deployment's SMTP quota". The address is the only
/// stable handle the caller offers, so the target account is what the limiter keys on.
/// </para>
/// <para>
/// <b>The wait must not be something a stranger can grow.</b> The caller is not the account, so a
/// counted backoff here is a lever: whoever knows the address runs it up, and the owner serves it
/// when they need the mail. A fixed cooldown that a refused request does not restart leaves nothing
/// to run up.
/// </para>
/// <para>
/// <b>The refusal must be invisible from outside.</b> Both forms already answer identically for
/// unknown and known addresses so as not to be enumeration oracles; an account inside its cooldown
/// has to get that same answer, or the cooldown itself becomes the existence signal.
/// </para>
/// </remarks>
public sealed class AccountEmailCooldownTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-email-cooldown-{Guid.NewGuid():N}.db");

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

    /// <summary>Each reset mail starts the cooldown on the account it is addressed to, and counts nothing.</summary>
    [Fact]
    public async Task AResetRequestStartsTheCooldownOnTheAccountItMails()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "cooled@example.com");
        CapturingEmailSender sender = new();
        FakeTimeProvider time = new(Start);

        await NewForgotModel(context, users, httpContext, sender, "cooled@example.com", time)
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle();

        UserActionAttempt attempt = await context.UserActionAttempts.AsNoTracking()
            .SingleAsync(a => a.UserId == user.Id && a.Action == LimitedAction.SendPasswordResetEmail,
                         TestContext.Current.CancellationToken);

        attempt.LockoutEnd.Should().Be(Start + ForgotPasswordModel.SendCooldown);
        attempt.FailedCount.Should().Be(0, "a count is what a stranger could run up");
    }

    /// <summary>
    /// An account inside its cooldown gets the identical redirect and no mail - the answer an unknown
    /// address gets, because a refusal that looked different would say the address is registered.
    /// </summary>
    [Fact]
    public async Task AnAccountInsideItsCooldownIsAnsweredIdenticallyAndGetsNoMail()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, "bombed@example.com");
        CapturingEmailSender sender = new();
        FakeTimeProvider time = new(Start);

        await NewForgotModel(context, users, httpContext, sender, "bombed@example.com", time)
            .OnPostAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(1));

        IActionResult result = await NewForgotModel(context, users, httpContext, sender, "bombed@example.com", time)
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle("the second request is inside the first one's cooldown");
        result.Should().BeOfType<RedirectToPageResult>()
              .Which.PageName.Should().Be("./ForgotPasswordConfirmation",
                                          "the refusal must be indistinguishable from a send");
    }

    /// <summary>
    /// Polling the form for somebody else's address neither grows their wait nor restarts it: a request
    /// a minute for an hour produces one mail per cooldown, and the owner is never further than one
    /// cooldown from the next.
    /// </summary>
    [Fact]
    public async Task PollingAnAddressNeitherGrowsNorRestartsItsWait()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "polled@example.com");
        CapturingEmailSender sender = new();
        FakeTimeProvider time = new(Start);

        for (int minute = 0; minute < 60; minute++)
        {
            await NewForgotModel(context, users, httpContext, sender, "polled@example.com", time)
                .OnPostAsync(TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromMinutes(1));
        }

        int expected = (int)(TimeSpan.FromHours(1) / ForgotPasswordModel.SendCooldown);

        sender.SentEmails.Should().HaveCount(expected, "a refused request must not push the next send back");

        TimeSpan? remaining = await NewLimiter(context).RemainingLockoutAsync(
            user.Id, LimitedAction.SendPasswordResetEmail, time.GetUtcNow(), TestContext.Current.CancellationToken);

        (remaining ?? TimeSpan.Zero).Should().BeLessThanOrEqualTo(ForgotPasswordModel.SendCooldown,
                                                                 "sixty requests must not have bought a longer wait than one");
    }

    /// <summary>An unknown address starts nothing, so the table cannot say what exists.</summary>
    [Fact]
    public async Task AnUnknownAddressWritesNothing()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        CapturingEmailSender sender = new();
        await NewForgotModel(context, users, httpContext, sender, "nobody@example.com", TimeProvider.System)
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().BeEmpty();
        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    /// <summary>
    /// The resend-confirmation form is held the same way, on its own row, and answers a send and a
    /// refusal with one sentence - which names the cooldown, so that it is true of both.
    /// </summary>
    [Fact]
    public async Task AResendStartsTheCooldownAndAnAccountInsideItGetsTheSameSentence()
    {
        using RequestCulture request = RequestCulture.English();
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "resend@example.com", confirmed: false);
        CapturingEmailSender sender = new();
        FakeTimeProvider time = new(Start);

        ResendEmailConfirmationModel sent = NewResendModel(context, users, httpContext, sender, "resend@example.com", time);
        await sent.OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle();

        UserActionAttempt attempt = await context.UserActionAttempts.AsNoTracking()
            .SingleAsync(a => a.UserId == user.Id && a.Action == LimitedAction.SendConfirmationEmail,
                         TestContext.Current.CancellationToken);

        attempt.LockoutEnd.Should().Be(Start + ResendEmailConfirmationModel.SendCooldown);

        time.Advance(TimeSpan.FromMinutes(1));

        ResendEmailConfirmationModel refused = NewResendModel(context, users, httpContext, sender, "resend@example.com", time);
        IActionResult result = await refused.OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle("the resend inside the cooldown must not mail");
        result.Should().BeOfType<PageResult>("the same page as a successful resend");

        string sentence = SentenceOf(sent);

        SentenceOf(refused).Should().Be(sentence, "the refusal must be indistinguishable from a send");
        sentence.Should().Contain($"{(int)ResendEmailConfirmationModel.SendCooldown.TotalMinutes} minutes",
                                  "the sentence is only true of a refusal if it says how recent the last mail can be");
    }

    /// <summary>
    /// The resend form is anonymous, so the browser asking need not be the account's owner's: the mail
    /// is written in the account's language, not the request's.
    /// </summary>
    [Fact]
    public async Task AResendIsWrittenInTheAccountsLanguage()
    {
        using RequestCulture request = RequestCulture.English();
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedUserAsync(users, "dane@example.com", confirmed: false);
        user.Language = "da";
        (await users.UpdateAsync(user)).Succeeded.Should().BeTrue();
        CapturingEmailSender sender = new();

        await NewResendModel(context, users, httpContext, sender, "dane@example.com", TimeProvider.System)
            .OnPostAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle().Which.subject.Should().Be("Bekræft din e-mailadresse");
    }

    private static string SentenceOf(ResendEmailConfirmationModel model)
    {
        return model.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage;
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
                                                      string email,
                                                      TimeProvider time)
    {
        return new ForgotPasswordModel(users, sender, NewLimiter(context), time,
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
                                                               string email,
                                                               TimeProvider time)
    {
        return new ResendEmailConfirmationModel(users, sender, NewLimiter(context), time,
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
