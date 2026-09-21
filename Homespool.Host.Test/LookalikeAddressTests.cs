using System;
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
/// An address spelled with a look-alike character is a different mailbox, wherever an address stands
/// in for proof of who someone is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Folding more than case is the whole problem.</b> Identity's lookup key is NFC then
/// <see cref="string.ToUpperInvariant"/>, and those two between them put exactly two characters an
/// address can contain onto ASCII letters: NFC maps the kelvin sign (U+212A) to K, and the uppercasing
/// maps a long s (U+017F) to S. So <c>ka&#x17F;per@example.net</c> finds the account stored as
/// <c>kasper@example.net</c>. Finding it is harmless; what matters is where the result goes.
/// </para>
/// <para>
/// <b>The characters are built from code points</b>, never written into a literal, so that nothing
/// between an editor and the compiler can quietly turn one into its ASCII twin and leave these tests
/// comparing an address with itself.
/// </para>
/// </remarks>
public sealed class LookalikeAddressTests : IDisposable
{
    private const string Stored = "kasper@example.net";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-lookalike-{Guid.NewGuid():N}.db");

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
    /// A reset asked for under a look-alike spelling is mailed to the account's own address, not the
    /// one typed - otherwise the reset token goes to whoever holds the look-alike mailbox.
    /// </summary>
    [Theory]
    [InlineData(0x017F, 's')]
    [InlineData(0x212A, 'k')]
    public async Task AResetIsMailedToTheStoredAddressWhateverSpellingFoundIt(int lookalike, char replaces)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, Stored, confirmed: true);
        CapturingEmailSender sender = new();

        ForgotPasswordModel model = new(users, sender, NewLimiter(context), TimeProvider.System, TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ForgotPasswordModel.InputModel { Email = Spell(Stored, replaces, lookalike) },
        };

        // Act
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert - one mail proves the look-alike found the account; its recipient is the point
        sender.SentEmails.Should().ContainSingle("the look-alike spelling finds the account")
              .Which.email.Should().Be(Stored, "the token belongs to the mailbox the account was confirmed at");
    }

    /// <summary>The confirmation resend is held to the same rule, for the same reason.</summary>
    [Theory]
    [InlineData(0x017F, 's')]
    [InlineData(0x212A, 'k')]
    public async Task AResendIsMailedToTheStoredAddressWhateverSpellingFoundIt(int lookalike, char replaces)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        await SeedUserAsync(users, Stored, confirmed: false);
        CapturingEmailSender sender = new();

        ResendEmailConfirmationModel model = new(users, sender, NewLimiter(context), TimeProvider.System, TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            Input = new ResendEmailConfirmationModel.InputModel { Email = Spell(Stored, replaces, lookalike) },
        };

        // Act
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        sender.SentEmails.Should().ContainSingle("the look-alike spelling finds the account")
              .Which.email.Should().Be(Stored);
    }

    /// <summary>
    /// <paramref name="address"/> with its first <paramref name="replaces"/> spelled as
    /// <paramref name="codePoint"/> instead.
    /// </summary>
    private static string Spell(string address, char replaces, int codePoint)
    {
        int at = address.IndexOf(replaces, StringComparison.Ordinal);

        return address[..at] + char.ConvertFromUtf32(codePoint) + address[(at + 1)..];
    }

    private static AttemptLimiter NewLimiter(HomespoolDbContext context)
    {
        return new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                  NullLogger<AttemptLimiter>.Instance);
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
