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

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Who the confirmation-resend form will mail, and who it answers without mailing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page exists for an account whose address is not confirmed yet</b>, and it used to test only
/// that the address belonged to an account at all. That made every registered address reachable
/// through an anonymous form, where only the unconfirmed ones have anything to confirm - and the link
/// a confirmed account received changed nothing when followed.
/// </para>
/// <para>
/// <b>Both refusals have to look like the send</b>, which is what these tests are really guarding:
/// the form answers an unknown address, a confirmed one and a genuine resend with the same page and
/// the same sentence, so that neither refusal becomes the answer to "is this address registered?".
/// The per-account backoff is held to the same rule in <c>AccountEmailBackoffTests</c>.
/// </para>
/// </remarks>
public sealed class ResendEmailConfirmationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-resend-{Guid.NewGuid():N}.db");

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

    /// <summary>An address with nothing left to confirm is answered, and nothing is sent.</summary>
    [Fact]
    public async Task AConfirmedAddressIsAnsweredWithoutMail()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, "settled@example.com", confirmed: true);
        CapturingEmailSender sender = new();

        // Act
        IActionResult result = await NewModel(context, users, httpContext, sender, "settled@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        sender.SentEmails.Should().BeEmpty("a confirmed address has nothing to confirm");
        result.Should().BeOfType<PageResult>("the same page an unknown address gets");
    }

    /// <summary>
    /// And it costs nothing: the refusal comes before the send is counted, so grinding at a confirmed
    /// address cannot arm that account's backoff and deny it a confirmation it might later need.
    /// </summary>
    [Fact]
    public async Task AConfirmedAddressIsNotCountedAgainstTheAccount()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, "settled@example.com", confirmed: true);
        CapturingEmailSender sender = new();

        // Act
        await NewModel(context, users, httpContext, sender, "settled@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        (await context.UserActionAttempts.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "nothing was spent, so nothing is counted");
    }

    /// <summary>The account the page is for still gets its mail.</summary>
    [Fact]
    public async Task AnUnconfirmedAddressIsStillMailed()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, "waiting@example.com", confirmed: false);
        CapturingEmailSender sender = new();

        // Act
        IActionResult result = await NewModel(context, users, httpContext, sender, "waiting@example.com")
            .OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        sender.SentEmails.Should().ContainSingle().Subject.email.Should().Be("waiting@example.com");
        result.Should().BeOfType<PageResult>("the same page both refusals get");
    }

    private static ResendEmailConfirmationModel NewModel(HomespoolDbContext context,
                                                         UserManager<HSUser> users,
                                                         DefaultHttpContext httpContext,
                                                         CapturingEmailSender sender,
                                                         string email)
    {
        AttemptLimiter limiter = new(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                     NullLogger<AttemptLimiter>.Instance);

        return new ResendEmailConfirmationModel(users, sender, limiter, TimeProvider.System,
                                                TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ResendEmailConfirmationModel.InputModel { Email = email },
        };
    }

    private static async Task<HSUser> SeedUserAsync(UserManager<HSUser> users, string email, bool confirmed)
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
