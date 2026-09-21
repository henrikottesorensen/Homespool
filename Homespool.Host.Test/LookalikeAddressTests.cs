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
using Homespool.Host.PrusaConnect;
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
    /// A provider asserting a look-alike address does not redeem the invitation issued to the real
    /// one. The provider's <c>email_verified</c> vouches for the look-alike mailbox, not the invited one.
    /// </summary>
    [Theory]
    [InlineData(0x017F, 's')]
    [InlineData(0x212A, 'k')]
    public async Task ALookalikeAddressDoesNotFindTheInvitation(int lookalike, char replaces)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitations = await InvitedAsync(context);

        // Act
        Invitation? found = await invitations.FindOutstandingForEmailAsync(
            Spell(Stored, replaces, lookalike), TestContext.Current.CancellationToken);

        // Assert
        found.Should().BeNull("a different mailbox is not the one the administrator invited");
    }

    /// <summary>
    /// And the fix did not turn into exact matching: people and providers still disagree about case.
    /// </summary>
    [Fact]
    public async Task TheInvitedAddressStillMatchesInAnyAsciiCase()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitations = await InvitedAsync(context);

        // Act
        Invitation? found = await invitations.FindOutstandingForEmailAsync(
            " Kasper@EXAMPLE.net ", TestContext.Current.CancellationToken);

        // Assert
        found.Should().NotBeNull();
        found!.Email.Should().Be(Stored);
    }

    /// <summary>
    /// The case of a Danish letter is still only case: an invitation written with å or Å is found by
    /// either spelling, and by the same spelling - which, with SQL doing the folding, a lowercase å
    /// never was.
    /// </summary>
    [Theory]
    [InlineData(0x00E5, 0x00E5)]
    [InlineData(0x00C5, 0x00E5)]
    [InlineData(0x00E5, 0x00C5)]
    [InlineData(0x00E6, 0x00C6)]
    [InlineData(0x00D8, 0x00F8)]
    public async Task AnInvitationIsFoundWhateverTheCaseOfANonAsciiLetter(int invited, int claimed)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        string address = char.ConvertFromUtf32(invited) + "se@example.net";
        InvitationService invitations = await InvitedAsync(context, address);

        // Act
        Invitation? found = await invitations.FindOutstandingForEmailAsync(
            char.ConvertFromUtf32(claimed) + "se@example.net", TestContext.Current.CancellationToken);

        // Assert
        found.Should().NotBeNull();
        found!.Email.Should().Be(address);
    }

    [Theory]
    [InlineData(0x00E5, 0x00C5)]
    [InlineData(0x00E6, 0x00C6)]
    [InlineData(0x00F8, 0x00D8)]
    [InlineData((int)'a', (int)'A')]
    public void LettersDifferingOnlyInCaseAreTheSameAddress(int lower, int upper)
    {
        EmailAddresses.SameAddress(char.ConvertFromUtf32(lower) + "@example.net",
                                   char.ConvertFromUtf32(upper) + "@EXAMPLE.NET")
                      .Should().BeTrue();
    }

    /// <summary>
    /// Each of these is folded onto an ASCII letter by one of the two invariant case mappings and
    /// not by the other, which is what the rule turns on.
    /// </summary>
    [Theory]
    [InlineData(0x017F, (int)'s')]
    [InlineData(0x017F, (int)'S')]
    [InlineData(0x212A, (int)'k')]
    [InlineData(0x212A, (int)'K')]
    [InlineData(0x0131, (int)'i')]
    [InlineData(0x0130, (int)'I')]
    public void ALookalikeIsNotTheSameAddress(int lookalike, int ascii)
    {
        EmailAddresses.SameAddress(char.ConvertFromUtf32(lookalike) + "@example.net",
                                   char.ConvertFromUtf32(ascii) + "@example.net")
                      .Should().BeFalse();
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

    private static async Task<InvitationService> InvitedAsync(HomespoolDbContext context, string address = Stored)
    {
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser administrator = await SeedUserAsync(users, "admin@example.net", confirmed: true);

        InvitationService invitations = new(context, new TokenService(), TestOptions.Snapshot(new InvitationOptions()));
        await invitations.CreateAsync(address, teamId: null, administrator.Id, expiresAt: null,
                                      TestContext.Current.CancellationToken);

        return invitations;
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
