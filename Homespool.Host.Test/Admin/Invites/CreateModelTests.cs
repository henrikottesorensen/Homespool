using System;
using System.IO;
using System.Linq;
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
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Pages.Admin.Invites;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The admin "create invitation" page: mints a token, mails the accept link, and shows it back
/// (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
public sealed class CreateModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-invite-create-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Builds a CreateModel wired to real services against <paramref name="context"/>, an admin already
    /// signed in unless <paramref name="signInAdmin"/> is false, and the fake Url/HttpContext plumbing
    /// unit-tested PageModels need.
    /// </summary>
    private static async Task<(CreateModel model, HSUser admin, CapturingEmailSender emailSender)> NewModelAsync(
        HSDbContext context, bool signInAdmin = true)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser admin = new() { UserName = "admin@example.com", Email = "admin@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(admin, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue("test setup must succeed: {0}", string.Join(", ", createResult.Errors.Select(e => e.Description)));

        if (signInAdmin)
        {
            IdentityTestHarness.SignInAsPrincipal(httpContext, admin);
        }

        InvitationService invitationService = new(context, new TokenService(), Options.Create(new InvitationOptions()));
        CapturingEmailSender emailSender = new();

        CreateModel model = new(invitationService, new TeamService(context), users, emailSender, NullLogger<CreateModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
        };

        return (model, admin, emailSender);
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        int queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        foreach (string pair in url[(queryStart + 1)..].Split('&'))
        {
            string[] parts = pair.Split('=', 2);
            if (parts[0] == key)
            {
                return parts.Length > 1 ? parts[1] : string.Empty;
            }
        }

        return null;
    }

    // ---------- OnGetAsync ----------

    /// <summary>The team picker always leads with "new account", followed by existing teams.</summary>
    [Fact]
    public async Task OnGetAsyncListsNewAccountFirstThenExistingTeams()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        context.Teams.Add(new Team { Name = "Print Squad", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        (CreateModel model, _, _) = await NewModelAsync(context);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.TeamOptions.Should().HaveCount(2);
        model.TeamOptions[0].Text.Should().Be("New account (its own team)");
        model.TeamOptions[0].Value.Should().BeEmpty();
        model.TeamOptions[1].Text.Should().Be("Print Squad");
    }

    // ---------- OnPostAsync happy path ----------

    /// <summary>
    /// A valid submission with no team creates a new-account invite, mails the accept link, reports it
    /// sent, and resets the form while keeping the link visible.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncCreatesANewAccountInviteAndMailsTheAcceptLink()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (CreateModel model, HSUser admin, CapturingEmailSender emailSender) = await NewModelAsync(context);
        model.Input.Email = "invitee@example.com";

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync();
        stored.Email.Should().Be("invitee@example.com");
        stored.TeamId.Should().BeNull();
        stored.InvitedBy.Should().Be(admin.Id);

        model.AcceptLink.Should().NotBeNullOrEmpty();
        model.AcceptLink.Should().Contain("/Account/Register");
        ExtractQueryValue(model.AcceptLink!, "inviteId").Should().Be(stored.Id.ToString());

        model.EmailSent.Should().BeTrue();
        emailSender.SentEmails.Should().ContainSingle(e => e.email == "invitee@example.com");

        model.Input.Email.Should().BeEmpty("the form resets for the next invite");
    }

    /// <summary>The accept link's code round-trips: decoding it validates the same invitation that was created.</summary>
    [Fact]
    public async Task TheAcceptLinksCodeValidatesAgainstTheCreatedInvitation()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (CreateModel model, _, _) = await NewModelAsync(context);
        model.Input.Email = "invitee@example.com";

        InvitationService invitationService = new(context, new TokenService(), Options.Create(new InvitationOptions()));

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync();
        string code = ExtractQueryValue(model.AcceptLink!, "code")!;
        string plaintext = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));

        (await invitationService.ValidateAsync(stored.Id, plaintext, CancellationToken.None)).Should().NotBeNull();
    }

    /// <summary>Selecting an existing team binds the invite to it instead of minting a new-account invite.</summary>
    [Fact]
    public async Task OnPostAsyncBindsTheInviteToTheSelectedTeam()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Team team = new() { Name = "Print Squad", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        (CreateModel model, _, _) = await NewModelAsync(context);
        model.Input.Email = "invitee@example.com";
        model.Input.TeamId = team.Id;

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync();
        stored.TeamId.Should().Be(team.Id);
    }

    /// <summary>An explicit expiry is honored instead of the configured default.</summary>
    [Fact]
    public async Task OnPostAsyncHonorsAnExplicitExpiry()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (CreateModel model, _, _) = await NewModelAsync(context);
        model.Input.Email = "invitee@example.com";
        model.Input.ExpiresInHours = 3;

        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync();
        stored.ExpiresAt.Should().BeOnOrAfter(before.AddHours(3)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddHours(3));
    }

    // ---------- OnPostAsync failure paths ----------

    /// <summary>An invalid model (missing email) returns the page without creating an invite.</summary>
    [Fact]
    public async Task OnPostAsyncWithInvalidModelStateCreatesNoInvite()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (CreateModel model, _, _) = await NewModelAsync(context);
        model.ModelState.AddModelError(nameof(CreateModel.InputModel.Email), "Required");

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        (await context.Invitations.CountAsync()).Should().Be(0);
        model.TeamOptions.Should().NotBeEmpty("options still reload even on a rejected submission");
    }

    /// <summary>
    /// No resolvable signed-in user - [Authorize] should make this unreachable in production - fails
    /// closed with Forbid rather than inventing an inviter id.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncForbidsWhenNoAdminUserResolves()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (CreateModel model, _, _) = await NewModelAsync(context, signInAdmin: false);
        model.Input.Email = "invitee@example.com";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<ForbidResult>();
        (await context.Invitations.CountAsync()).Should().Be(0);
    }
}
