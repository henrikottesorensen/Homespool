using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The cooldown on the two sends of the signed-in address page.
/// </summary>
/// <remarks>
/// <b>The change link is the one mail a signed-in account can address to anybody.</b> The recent proof
/// decides who may send it; these pin how often - one send per button per minute, with neither button
/// holding the other off, and nothing spent by a post that sends nothing. The proof filter is not
/// exercised here: the handlers are called directly, which is the state after it has let a post through.
/// </remarks>
public sealed class ManageEmailCooldownTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>A second change link inside the minute is refused, says why, and mails nobody.</summary>
    [Fact]
    public async Task ASecondChangeLinkWithinTheMinuteIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = await SeedSignedInUserAsync(users, httpContext, "owner@example.com");
        FakeTimeProvider time = new(Start);
        CapturingEmailSender sender = new();

        await NewModel(context, users, httpContext, sender, time, "first@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(59));

        EmailModel second = NewModel(context, users, httpContext, sender, time, "second@example.com");
        await second.OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle("the second post is inside the cooldown")
              .Which.email.Should().Be("first@example.com");
        second.StatusMessage.Should().Be(TestLocaliser.Shared()["Manage_EmailSendCooldown"].Value);

        UserActionAttempt row = await context.UserActionAttempts.AsNoTracking()
            .SingleAsync(a => a.UserId == user.Id && a.Action == LimitedAction.ChangeEmail,
                         TestContext.Current.CancellationToken);

        row.FailedCount.Should().Be(0, "a cooldown counts nothing, so it can never grow into a longer wait");
        row.LockoutEnd.Should().Be(Start + EmailModel.SendCooldown,
                                   "the refused post must not have pushed the cooldown further out");
    }

    /// <summary>Once the minute has passed, the next change link goes.</summary>
    [Fact]
    public async Task AChangeLinkMaySendAgainOnceTheMinuteHasPassed()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedSignedInUserAsync(users, httpContext, "patient@example.com");
        FakeTimeProvider time = new(Start);
        CapturingEmailSender sender = new();

        await NewModel(context, users, httpContext, sender, time, "first@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        time.Advance(EmailModel.SendCooldown);

        await NewModel(context, users, httpContext, sender, time, "second@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().HaveCount(2);
    }

    /// <summary>The verification resend is held off the same way, on its own button.</summary>
    [Fact]
    public async Task ASecondVerificationEmailWithinTheMinuteIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedSignedInUserAsync(users, httpContext, "unverified@example.com");
        FakeTimeProvider time = new(Start);
        CapturingEmailSender sender = new();

        for (int i = 0; i < 5; i++)
        {
            await NewModel(context, users, httpContext, sender, time, newEmail: null)
                .OnPostSendVerificationEmailAsync(TestContext.Current.CancellationToken);
        }

        sender.SentEmails.Should().ContainSingle("five posts inside a minute must not become five emails");
    }

    /// <summary>
    /// Sending a verification does not hold off correcting the address, nor the other way round: each
    /// button has its own cooldown.
    /// </summary>
    [Fact]
    public async Task TheTwoButtonsDoNotHoldEachOtherOff()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedSignedInUserAsync(users, httpContext, "both@example.com");
        FakeTimeProvider time = new(Start);
        CapturingEmailSender sender = new();

        await NewModel(context, users, httpContext, sender, time, newEmail: null)
            .OnPostSendVerificationEmailAsync(TestContext.Current.CancellationToken);
        await NewModel(context, users, httpContext, sender, time, "corrected@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().HaveCount(2);
    }

    /// <summary>
    /// Submitting the address the account already has sends nothing, so it starts no cooldown and the
    /// real change straight after it still goes.
    /// </summary>
    [Fact]
    public async Task AnUnchangedAddressStartsNoCooldown()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedSignedInUserAsync(users, httpContext, "same@example.com");
        FakeTimeProvider time = new(Start);
        CapturingEmailSender sender = new();

        await NewModel(context, users, httpContext, sender, time, "same@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0);

        await NewModel(context, users, httpContext, sender, time, "different@example.com")
            .OnPostChangeEmailAsync(TestContext.Current.CancellationToken);

        sender.SentEmails.Should().ContainSingle();
    }

    private static EmailModel NewModel(HomespoolDbContext context,
                                       UserManager<HSUser> users,
                                       DefaultHttpContext httpContext,
                                       CapturingEmailSender sender,
                                       FakeTimeProvider time,
                                       string? newEmail)
    {
        AttemptLimiter limiter = new(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                     NullLogger<AttemptLimiter>.Instance);

        return new EmailModel(users, sender, new RecentProof(new EphemeralDataProtectionProvider(), time),
                              limiter, time, TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new EmailModel.InputModel { NewEmail = newEmail },
        };
    }

    private static async Task<HSUser> SeedSignedInUserAsync(UserManager<HSUser> users,
                                                            DefaultHttpContext httpContext,
                                                            string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue(); // betterleaks:allow

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

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
