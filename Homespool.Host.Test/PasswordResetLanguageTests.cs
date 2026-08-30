using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// An email written to somebody who is not the one making the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Password reset is the clearest case in the application where the request's culture is the
/// wrong one.</b> The page is anonymous, so the browser asking belongs to whoever typed the address
/// into the form - which need not be the person whose inbox the message lands in, and is not a
/// signed-in account whose preference the middleware could have resolved. The only thing that knows
/// what language to write in is the stored column.
/// </para>
/// <para>
/// This is the second caller of that column, after <c>TelemetryAlertService</c>, and the first on a
/// request path. The distinction it guards is easy to lose in a refactor: everything still compiles
/// and every email still sends if the <c>InCulture</c> wrapper is dropped - they just arrive in the
/// wrong language, for the subset of people who did not ask for them.
/// </para>
/// </remarks>
public sealed class PasswordResetLanguageTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-reset-language-{Guid.NewGuid():N}.db");

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
    /// The account's language wins over the browser's, because the two can belong to different people.
    /// </summary>
    [Fact]
    public async Task TheResetEmailIsWrittenInTheAccountsLanguage()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        CapturingEmailSender sender = await PostResetRequestAsync(context, "dane@example.com", "da");

        sender.SentEmails.Should().ContainSingle();
        sender.SentEmails[0].subject.Should().Be("Nulstil din adgangskode");
        sender.SentEmails[0].htmlMessage.Should().Contain("klikke her");
    }

    /// <summary>
    /// No stored choice means the deployment default, not a guess from the requesting browser - there
    /// is nothing to say that browser belongs to the account holder.
    /// </summary>
    [Fact]
    public async Task NoStoredChoiceLeavesTheDefaultStanding()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        CapturingEmailSender sender = await PostResetRequestAsync(context, "brit@example.com", language: null);

        sender.SentEmails.Should().ContainSingle();
        sender.SentEmails[0].subject.Should().Be("Reset your password");
    }

    /// <summary>
    /// Composing in another culture must not leave the thread in it, or the next page this request
    /// renders is Danish for somebody who is not.
    /// </summary>
    [Fact]
    public async Task TheRequestsOwnCultureSurvivesComposing()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        CultureInfo before = CultureInfo.CurrentUICulture;
        _ = await PostResetRequestAsync(context, "dane@example.com", "da");

        CultureInfo.CurrentUICulture.Should().Be(before);
    }

    private async Task<CapturingEmailSender> PostResetRequestAsync(
        HomespoolDbContext context,
        string email,
        string? language)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new(email.Split('@')[0])
        {
            Email = email,
            EmailConfirmed = true,
            Language = language,
        };

        // With a password, and that is load-bearing rather than incidental: ForgotPassword refuses an
        // account that has none, because such an account signs in with an external provider and must
        // not be able to acquire a password by mail. Created without one, this fixture asks for a
        // reset that is correctly never sent, and the language assertion below has nothing to read.
        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue(); // betterleaks:allow

        CapturingEmailSender sender = new();
        ForgotPasswordModel model = new(users, sender,
                                        new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                                           NullLogger<AttemptLimiter>.Instance),
                                        TimeProvider.System,
                                        TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ForgotPasswordModel.InputModel { Email = email },
        };

        await model.OnPostAsync(TestContext.Current.CancellationToken);

        return sender;
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
