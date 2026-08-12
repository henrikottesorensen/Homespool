using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Pages.Account;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The invite-accept flow: creates the account bound to the invite's email, joins a team when the
/// invite names one, and spends the invite atomically with the account creation
/// (AGENT-NOTES phase-1.5 §15 step 6). Replaces the open self-service registration the Identity
/// scaffold shipped with.
/// </summary>
public sealed class RegisterModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-register-{Guid.NewGuid():N}.db");

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

    private static InvitationService NewInvitationService(HomespoolDbContext context)
    {
        return new(context, new TokenService(), Options.Create(new InvitationOptions()));
    }

    /// <summary>
    /// Builds a RegisterModel wired to real services against <paramref name="context"/>, with a real
    /// UserManager/SignInManager from the identity harness. <paramref name="smtpConfigured"/> drives
    /// AccountConfirmationPolicy exactly as Program.cs does - confirmed at creation only when SMTP is
    /// absent.
    /// </summary>
    private static (RegisterModel model, DefaultHttpContext httpContext, CapturingEmailSender emailSender) NewModel(
        HomespoolDbContext context,
        InvitationService invitationService,
        bool smtpConfigured)
    {
        (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, IServiceProvider provider) =
            IdentityTestHarness.BuildIdentityServices(context);

        AccountConfirmationPolicy confirmationPolicy = new(
            Options.Create(new SmtpOptions { Host = smtpConfigured ? "smtp.example.com" : string.Empty }));
        CapturingEmailSender emailSender = new();

        RegisterModel model = new(
            users,
            provider.GetRequiredService<IUserStore<HSUser>>(),
            signIn,
            NullLogger<RegisterModel>.Instance,
            emailSender,
            confirmationPolicy,
            invitationService,
            new TeamService(context),
            new UnitOfWork(context),
            TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
        };

        return (model, httpContext, emailSender);
    }

    private static string EncodeCode(string plaintext)
    {
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(plaintext));
    }

    private static void SetInvite(RegisterModel model, int inviteId, string plaintextToken)
    {
        model.InviteId = inviteId;
        model.Code = EncodeCode(plaintextToken);
    }

    /// <summary>
    /// Fills in everything the form asks the invitee for. The username is theirs to pick - the invite
    /// binds only the address - so it is part of a valid post rather than something derived here.
    /// </summary>
    private static void SetValidInput(RegisterModel model, string password = "Sup3rSecret!23", string username = "invitee")
    {
        model.Input = new RegisterModel.InputModel
        {
            Username = username,
            Password = password,
            ConfirmPassword = password,
        };
    }

    // ---------- OnGetAsync ----------

    /// <summary>A valid, unexpired, unused invite renders the form with the invite's bound email.</summary>
    [Fact]
    public async Task OnGetAsyncWithAValidInviteIsValidAndShowsItsEmail()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);

        // Act
        await model.OnGetAsync(returnUrl: null, CancellationToken.None);

        // Assert
        model.InviteValid.Should().BeTrue();
        model.Email.Should().Be("invitee@example.com");
    }

    /// <summary>An unknown invite id, a wrong token, an expired invite and an already-used invite all render as invalid.</summary>
    [Fact]
    public async Task OnGetAsyncIsInvalidForAnUnknownWrongExpiredOrUsedInvite()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);

        (Invitation outstanding, string outstandingToken) = await invitationService.CreateAsync(
            "wrong-token@example.com", null, 1, null, CancellationToken.None);

        (Invitation expired, string expiredToken) = await invitationService.CreateAsync(
            "expired@example.com", null, 1, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        (Invitation used, string usedToken) = await invitationService.CreateAsync(
            "used@example.com", null, 1, null, CancellationToken.None);
        Invitation usedTracked = (await invitationService.ValidateAsync(used.Id, usedToken, CancellationToken.None))!;
        await invitationService.MarkUsedAsync(usedTracked, CancellationToken.None);

        // Assert: unknown id
        (RegisterModel unknown, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(unknown, -1, outstandingToken);
        await unknown.OnGetAsync(null, CancellationToken.None);
        unknown.InviteValid.Should().BeFalse();

        // Assert: wrong token
        (RegisterModel wrongToken, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(wrongToken, outstanding.Id, "not-the-right-token");
        await wrongToken.OnGetAsync(null, CancellationToken.None);
        wrongToken.InviteValid.Should().BeFalse();

        // Assert: expired
        (RegisterModel expiredModel, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(expiredModel, expired.Id, expiredToken);
        await expiredModel.OnGetAsync(null, CancellationToken.None);
        expiredModel.InviteValid.Should().BeFalse();

        // Assert: used
        (RegisterModel usedModel, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(usedModel, used.Id, usedToken);
        await usedModel.OnGetAsync(null, CancellationToken.None);
        usedModel.InviteValid.Should().BeFalse();
    }

    // ---------- OnPostAsync happy paths ----------

    /// <summary>
    /// SMTP unconfigured: the account is confirmed at creation (AccountConfirmationPolicy), so accept
    /// signs the user straight in and redirects locally, and the invite is spent.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithSmtpUnconfiguredSignsInDirectlyAndSpendsTheInvite()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, DefaultHttpContext httpContext, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        result.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be("/dashboard");

        HSUser stored =
            await context.Users.SingleAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken);
        stored.EmailConfirmed.Should().BeTrue("no SMTP means no confirmation mail can ever arrive");

        httpContext.Response.Headers.Should().ContainKey("Set-Cookie", "signing in writes the auth cookie");

        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should()
            .BeNull("the invite is spent");
    }

    /// <summary>
    /// SMTP configured: the account is not confirmed at creation, so accept sends a confirmation mail
    /// and holds at RegisterConfirmation instead of signing in - but the invite is spent either way.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithSmtpConfiguredSendsConfirmationAndSpendsTheInvite()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, CapturingEmailSender emailSender) = NewModel(context, invitationService, smtpConfigured: true);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        RedirectToPageResult redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("RegisterConfirmation");

        HSUser stored =
            await context.Users.SingleAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken);
        stored.EmailConfirmed.Should().BeFalse("SMTP is configured, so a confirmation mail is expected to arrive");

        emailSender.SentEmails.Should().ContainSingle(e => e.email == "invitee@example.com" && e.subject == "Confirm your email");

        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should()
            .BeNull("the invite is spent regardless of the confirmation path");
    }

    // ---------- team membership ----------

    /// <summary>A team-bound invite joins that team in addition to minting the user's own default team.</summary>
    [Fact]
    public async Task OnPostAsyncWithATeamBoundInviteJoinsThatTeamAndGetsItsOwnDefaultTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        Team existingTeam = new() { Name = "Print Squad", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(existingTeam);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", existingTeam.Id, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        HSUser user = await context.Users.SingleAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken);
        List<TeamMember> memberships = await context.TeamMembers.Where(m => m.UserId == user.Id)
                                                    .ToListAsync(TestContext.Current.CancellationToken);

        memberships.Should().HaveCount(2, "the user's own default team plus the invited team");
        memberships.Should().ContainSingle(m => m.IsDefault && m.TeamId != existingTeam.Id,
                                           "the default team is a fresh one, not the invited team");
        memberships.Should()
                   .ContainSingle(m => m.TeamId == existingTeam.Id && !m.IsDefault && m.CanRead && m.CanUse && !m.CanManage);
    }

    /// <summary>A new-account invite (no team) yields only the default team, no extra membership.</summary>
    [Fact]
    public async Task OnPostAsyncWithANewAccountInviteYieldsOnlyTheDefaultTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        HSUser user = await context.Users.SingleAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken);
        List<TeamMember> memberships = await context.TeamMembers.Where(m => m.UserId == user.Id)
                                                    .ToListAsync(TestContext.Current.CancellationToken);

        memberships.Should().ContainSingle();
        memberships[0].IsDefault.Should().BeTrue();
    }

    // ---------- re-validation and failure paths ----------

    /// <summary>
    /// The invite is re-validated on POST: if it was spent (or expired) between the GET that rendered
    /// the form and the submit, no account is created - guards the race between render and submit.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncRevalidatesAndCreatesNoAccountIfTheInviteWasSpentSinceTheGet()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        await model.OnGetAsync(null, CancellationToken.None);
        model.InviteValid.Should().BeTrue("sanity check: the invite was good at GET time");

        // Someone else raced this invite between the GET and the submit.
        Invitation tracked = (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None))!;
        await invitationService.MarkUsedAsync(tracked, CancellationToken.None);

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.InviteValid.Should().BeFalse();
        (await context.Users.CountAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken)).Should()
            .Be(0);
    }

    /// <summary>An invalid submission (e.g. a password mismatch) redisplays the form without creating an account.</summary>
    [Fact]
    public async Task OnPostAsyncWithInvalidModelStateCreatesNoAccountAndLeavesTheInviteUnspent()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);
        model.ModelState.AddModelError(nameof(RegisterModel.InputModel.ConfirmPassword),
                                       "The password and confirmation password do not match.");

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.InviteValid.Should().BeTrue("the invite itself was fine - only the submitted form was rejected");

        (await context.Users.CountAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken)).Should()
            .Be(0);
        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should()
            .NotBeNull("an invite is never spent by a rejected submission");
    }

    /// <summary>
    /// Identity's own uniqueness check failing (e.g. the email/username is somehow already taken) fails
    /// closed: no account created, the invite stays unspent, and the identity errors surface.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithADuplicateEmailCreatesNoAccountAndLeavesTheInviteUnspent()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        (UserManager<HSUser> preexistingUsers, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser existing = new("existing") { Email = "invitee@example.com", EmailConfirmed = true };
        (await preexistingUsers.CreateAsync(existing, "Sup3rSecret!23")).Succeeded.Should().BeTrue("test setup");

        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();

        (await context.Users.CountAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken)).Should()
            .Be(1, "no second row for the duplicate email");
        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should()
            .NotBeNull("the failed attempt must not spend the invite");
    }

    /// <summary>
    /// A failure deeper in the transaction (here: the database's own "at most one default team per
    /// user" constraint, forced by pre-seeding the row the new user's id is about to collide with) rolls
    /// back the whole accept: no user row survives, and the invite is still unspent. This is the
    /// guarantee the single shared transaction exists for.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncRollsBackTheWholeAcceptIfAConstraintFailsPartwayThrough()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        (UserManager<HSUser> placeholderUsers, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser placeholder = new("placeholder") { Email = "placeholder@example.com", EmailConfirmed = true };
        (await placeholderUsers.CreateAsync(placeholder, "Sup3rSecret!23")).Succeeded.Should().BeTrue("test setup");

        // The next user Identity creates will get this id (SQLite rowids are sequential), so
        // pre-occupying its one allowed default-team slot forces AddDefaultTeamAsync to collide with
        // the unique filtered index on (UserId) WHERE IsDefault, deep inside the accept transaction.
        long nextUserId = placeholder.Id + 1;

        Team dummyTeam = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(dummyTeam);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = dummyTeam.Id,
            UserId = nextUserId,
            CanRead = true,
            CanUse = true,
            CanManage = true,
            IsDefault = true,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        InvitationService invitationService = NewInvitationService(context);
        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        (RegisterModel model, _, _) = NewModel(context, invitationService, smtpConfigured: false);
        SetInvite(model, invitation.Id, plaintext);
        SetValidInput(model);

        // Act
        IActionResult result = await model.OnPostAsync("/dashboard", CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        model.ModelState[string.Empty]!.Errors.Should().ContainSingle(e => e.ErrorMessage.Contains("try again"));

        (await context.Users.CountAsync(u => u.Email == "invitee@example.com", TestContext.Current.CancellationToken)).Should()
            .Be(0, "the whole transaction rolled back, including the user row");
        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should()
            .NotBeNull("a rolled-back accept must not spend the invite");
    }
}
