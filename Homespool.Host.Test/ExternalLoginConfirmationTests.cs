using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Pages.Account;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The external sign-in's confirmation post: an invite carried through the provider creates exactly one
/// account, bound to the invite's address and linked to the provider's subject, and every way that can
/// fail leaves no account and no spent invite behind.
/// </summary>
/// <remarks>
/// The provider's answer is written into the external cookie in-process, with the properties the page's
/// own challenge built, so the invite reaches the confirmation the way it would through a real provider.
/// The protocol conversation itself is the dex tests' job; this is everything after the answer is in.
/// </remarks>
public sealed class ExternalLoginConfirmationTests : IDisposable
{
    private const string Issuer = "https://provider.example.net";

    private const string Subject = "provider-subject";

    private const string InvitedEmail = "invitee@example.com";

    private const string ReturnUrl = "/dashboard";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-external-confirm-{Guid.NewGuid():N}.db");

    private readonly IStringLocalizer<SharedResource> _localiser = TestLocaliser.Shared();

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

    // ---------- an account is created ----------
    [Fact]
    public async Task AConfirmationCreatesTheInvitedAccountLinksTheProviderAndSpendsTheInvite()
    {
        // Arrange
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        // Act
        IActionResult result = await ConfirmAsync(page, "kilgore");

        // Assert
        result.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be(ReturnUrl);
        rig.CookieOf((DefaultHttpContext)page.HttpContext, IdentityConstants.ApplicationScheme).Should().NotBeNullOrEmpty("a confirmed account is signed in");

        HSUser created = (await rig.Users.FindByEmailAsync(InvitedEmail))!;
        created.UserName.Should().Be("kilgore");
        (await rig.Users.GetLoginsAsync(created)).Should().ContainSingle()
            .Which.ProviderKey.Should().Be(ExternalSignIn.ProviderKey(Issuer, Subject));

        (await Invitations(rig).ValidateAsync(invitation.Uuid, token, [InvitationType.Signup], CancellationToken.None)).Should()
            .BeNull("accepting through a provider spends the invite");
    }

    /// <summary>
    /// On the token door the provider's address is never consulted: the account is the invite's, whatever
    /// the provider says about the person, verified or not.
    /// </summary>
    [Fact]
    public async Task TheAccountIsBoundToTheInvitesAddressNotTheProviders()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token, providerEmail: "someone-else@example.com");

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        await ConfirmAsync(page, "kilgore");

        (await rig.Users.FindByEmailAsync(InvitedEmail)).Should().NotBeNull();
        (await rig.Users.FindByEmailAsync("someone-else@example.com")).Should().BeNull("the provider's address authorises nothing here");
    }

    /// <summary>
    /// SMTP configured: the account is unconfirmed and holds at <c>RegisterConfirmation</c>, as on Register -
    /// the provider having verified an address does not shortcut the one confirmation rule. The mail goes to
    /// the invite's address, and a send that failed is reported rather than hidden.
    /// </summary>
    [Theory]
    [InlineData(EmailSendResult.Sent, false)]
    [InlineData(EmailSendResult.Failed, true)]
    public async Task WithSmtpConfiguredTheAccountHoldsForConfirmation(EmailSendResult sent, bool reportedFailed)
    {
        // Arrange
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: true, out CapturingEmailSender mail);
        mail.Result = sent;

        // Act
        IActionResult result = await ConfirmAsync(page, "kilgore");

        // Assert
        RedirectToPageResult redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("./RegisterConfirmation");
        redirect.RouteValues!["Email"].Should().Be(InvitedEmail);
        redirect.RouteValues["emailFailed"].Should().Be(reportedFailed);

        ((DefaultHttpContext)page.HttpContext).Response.Headers.SetCookie.Should()
            .NotContain(cookie => cookie!.StartsWith(rig.CookieNameOf(IdentityConstants.ApplicationScheme) + "=", StringComparison.Ordinal),
                        "an unconfirmed account is not signed in");

        (await rig.Users.FindByEmailAsync(InvitedEmail))!.EmailConfirmed.Should().BeFalse();
        mail.SentEmails.Should().ContainSingle(email => email.email == InvitedEmail && email.subject == "Confirm your email");
    }

    /// <summary>A team-bound invite joins that team as an operator, in addition to the account's own default team.</summary>
    [Fact]
    public async Task ATeamBoundInviteJoinsThatTeamAsWellAsTheAccountsOwn()
    {
        // Arrange
        await using LocalSchemeRig rig = await NewRigAsync();

        Team team = new() { Name = "Print Squad", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        rig.Context.Teams.Add(team);
        await rig.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (Invitation invitation, string token) = await InviteAsync(rig, team.Id);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        // Act
        await ConfirmAsync(page, "kilgore");

        // Assert
        HSUser created = (await rig.Users.FindByEmailAsync(InvitedEmail))!;
        List<TeamMember> memberships = await rig.Context.TeamMembers.Where(member => member.UserId == created.Id)
                                                .ToListAsync(TestContext.Current.CancellationToken);

        memberships.Should().HaveCount(2);
        memberships.Should().ContainSingle(member => member.IsDefault && member.TeamId != team.Id);
        memberships.Should().ContainSingle(member => member.TeamId == team.Id &&
                                                     !member.IsDefault &&
                                                     member.Capabilities == CapabilitySet.Format(CapabilityPresets.Operator));
    }

    // ---------- nothing is created ----------
    [Fact]
    public async Task WithNoProviderAnswerNothingIsCreated()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(), smtpConfigured: false, out _);

        IActionResult result = await ConfirmAsync(page, "kilgore");

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("./Login");
        page.ErrorMessage.Should().Be(_localiser["Account_ExternalLoginConfirmError"].Value);
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    /// <summary>
    /// An answer to a link, carrying the same invite, is not a sign-in's: the confirmation reads only the
    /// flow that asked for it, so another flow's answer cannot be turned into a new account.
    /// </summary>
    [Fact]
    public async Task AnotherFlowsAnswerCreatesNothing()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        HSUser linking = await rig.AddUserAsync("linking@example.com");
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        AuthenticationProperties properties = await ChallengePropertiesAsync(rig, invitation, token);
        properties.Items[ExternalSignIn.RoundTripItem] = nameof(ExternalRoundTrip.Link);
        properties.Items[ExternalSignIn.ExpectedAccountItem] = linking.Id.ToString();

        string answer = await AnswerAsync(rig, properties);
        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        IActionResult result = await ConfirmAsync(page, "kilgore");

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("./Login");
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    /// <summary>
    /// The invite is re-resolved on the post: one spent while the form sat on screen creates nothing, and
    /// the person is sent back with the same message as a sign-in that never had one.
    /// </summary>
    [Fact]
    public async Task AnInviteSpentSinceTheFormWasShownCreatesNothing()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        InvitationService invitations = Invitations(rig);
        await invitations.MarkUsedAsync((await invitations.ValidateAsync(invitation.Uuid, token, [InvitationType.Signup], CancellationToken.None))!,
                                        CancellationToken.None);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        IActionResult result = await ConfirmAsync(page, "kilgore");

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("./Login");
        page.ErrorMessage.Should().Be(_localiser["Account_ExternalNoInvite"].Value);
        (await rig.Users.FindByEmailAsync(InvitedEmail)).Should().BeNull();
    }

    [Fact]
    public async Task AnInvalidFormCreatesNothingAndShowsTheInvitesAddress()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        // A username Identity would accept, so only the form's own validation stands between it and an account.
        page.ModelState.AddModelError("Input.Username", "The field Username must be a string with a maximum length of 256.");

        IActionResult result = await ConfirmAsync(page, "kilgore");

        result.Should().BeOfType<PageResult>();
        page.Email.Should().Be(InvitedEmail, "the form shows again with the address the account will have");
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    /// <summary>Identity's own refusal - a username somebody holds - is shown on the form, and nothing is created or spent.</summary>
    [Fact]
    public async Task ATakenUsernameCreatesNothing()
    {
        await using LocalSchemeRig rig = await NewRigAsync();
        await rig.AddUserAsync("kilgore@example.com");
        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        IActionResult result = await ConfirmAsync(page, "kilgore");

        result.Should().BeOfType<PageResult>();
        page.ModelState[string.Empty]!.Errors.Should().NotBeEmpty("Identity's error is surfaced on the form");
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    /// <summary>
    /// The provider identity linked to another account while the form was on screen - a second tab, say.
    /// The account row is already written when the link is refused, and the transaction is what takes it
    /// back: an account with no login attached is one nobody can sign in to.
    /// </summary>
    [Fact]
    public async Task AProviderIdentityLinkedElsewhereLeavesNoAccountBehind()
    {
        // Arrange
        await using LocalSchemeRig rig = await NewRigAsync();
        HSUser holder = await rig.AddUserAsync("holder@example.com");
        (await rig.Users.AddLoginAsync(holder, new UserLoginInfo(Schemes.ExternalOidc, ExternalSignIn.ProviderKey(Issuer, Subject), "Provider")))
            .Succeeded.Should().BeTrue();

        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        // Act
        IActionResult result = await ConfirmAsync(page, "kilgore");

        // Assert
        result.Should().BeOfType<PageResult>();
        page.ModelState[string.Empty]!.Errors.Should().NotBeEmpty();
        (await rig.Context.Users.CountAsync(user => user.UserName == "kilgore", TestContext.Current.CancellationToken)).Should()
            .Be(0, "the account written before the link was refused is rolled back with it");
        (await rig.Users.FindByLoginAsync(Schemes.ExternalOidc, ExternalSignIn.ProviderKey(Issuer, Subject)))!.Id.Should().Be(holder.Id);
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    /// <summary>
    /// A failure deeper in the transaction - the one-default-team-per-account constraint, forced by
    /// pre-seeding the row the new account's id is about to collide with - rolls back the account, its
    /// login and the invite together, and says so on the form.
    /// </summary>
    [Fact]
    public async Task AConstraintFailingPartwayRollsBackTheWholeAccept()
    {
        // Arrange
        await using LocalSchemeRig rig = await NewRigAsync();
        HSUser placeholder = await rig.AddUserAsync("placeholder@example.com");

        // SQLite hands out rowids in sequence, so the next account is this id.
        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        rig.Context.Teams.Add(team);
        await rig.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        rig.Context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = placeholder.Id + 1,
            Capabilities = TestMemberships.Literal(CapabilityPresets.Manager),
            IsDefault = true,
        });
        await rig.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (Invitation invitation, string token) = await InviteAsync(rig, teamId: null);
        string answer = await AnswerAsync(rig, invitation, token);

        ExternalLoginModel page = NewPage(rig, rig.NewRequest(answer), smtpConfigured: false, out _);

        // Act
        IActionResult result = await ConfirmAsync(page, "kilgore");

        // Assert
        result.Should().BeOfType<PageResult>();
        page.ModelState[string.Empty]!.Errors.Should().ContainSingle(error => error.ErrorMessage == _localiser["Account_RegistrationFailed"].Value);
        (await rig.Context.UserLogins.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0, "the login is rolled back with the account");
        await ShouldHaveCreatedNothingAsync(rig, invitation, token);
    }

    // ---------- the rig ----------

    /// <summary>A rig with a display-named provider registered, which is what makes it one the login page offers.</summary>
    private Task<LocalSchemeRig> NewRigAsync()
    {
        return LocalSchemeRig.CreateAsync(_databasePath,
                                          services => services.Configure<AuthenticationOptions>(
                                              options => options.AddScheme(Schemes.ExternalOidc, scheme =>
                                              {
                                                  scheme.DisplayName = "Provider";
                                                  scheme.HandlerType = typeof(NeverRunHandler);
                                              })));
    }

    private static InvitationService Invitations(LocalSchemeRig rig)
    {
        return new InvitationService(rig.Context, new TokenService(), TestOptions.Snapshot(new InvitationOptions()), TimeProvider.System);
    }

    private static Task<(Invitation invitation, string token)> InviteAsync(LocalSchemeRig rig, int? teamId)
    {
        return Invitations(rig).CreateAsync(InvitedEmail, teamId, invitedBy: 1, expiresAt: null, CancellationToken.None);
    }

    /// <summary>The page over <paramref name="request"/>, wired to the rig's own services.</summary>
    private ExternalLoginModel NewPage(LocalSchemeRig rig, DefaultHttpContext request, bool smtpConfigured, out CapturingEmailSender mail)
    {
        request.Response.Body = new MemoryStream();

        IServiceProvider services = request.RequestServices;
        mail = new CapturingEmailSender();

        ExternalLoginModel page = new(services.GetRequiredService<LocalSignIn>(),
                                      services.GetRequiredService<ExternalSignIn>(),
                                      services.GetRequiredService<UserManager<HSUser>>(),
                                      services.GetRequiredService<IUserStore<HSUser>>(),
                                      NullLogger<ExternalLoginModel>.Instance,
                                      mail,
                                      new AccountConfirmationPolicy(Options.Create(new SmtpOptions { Host = smtpConfigured ? "smtp.example.com" : string.Empty })),
                                      Invitations(rig),
                                      new TeamService(rig.Context),
                                      new UnitOfWork(rig.Context),
                                      Options.Create(new OidcOptions()),
                                      TimeProvider.System, _localiser)
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Url = IdentityTestHarness.NewUrlHelper(request),
        };

        // The challenge builds its callback address relative to the page it is on.
        page.Url.ActionContext.RouteData.Values["page"] = "/Account/ExternalLogin";

        return page;
    }

    /// <summary>
    /// The properties the page's own challenge carries the invite in - taken from the challenge rather than
    /// restated, so this cannot drift from what the page writes.
    /// </summary>
    private async Task<AuthenticationProperties> ChallengePropertiesAsync(LocalSchemeRig rig, Invitation invitation, string token)
    {
        ExternalLoginModel page = NewPage(rig, rig.NewRequest(), smtpConfigured: false, out _);

        IActionResult challenge = await page.OnPostAsync(Schemes.ExternalOidc, ReturnUrl, invitation.Uuid,
                                                         WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));

        return challenge.Should().BeOfType<ChallengeResult>().Subject.Properties!;
    }

    /// <summary>The external cookie a provider's answer to a sign-in carrying this invite leaves behind.</summary>
    private async Task<string> AnswerAsync(LocalSchemeRig rig, Invitation invitation, string token, string? providerEmail = null)
    {
        return await AnswerAsync(rig, await ChallengePropertiesAsync(rig, invitation, token), providerEmail);
    }

    private static async Task<string> AnswerAsync(LocalSchemeRig rig, AuthenticationProperties properties, string? providerEmail = null)
    {
        List<Claim> claims = [new Claim(JwtClaimTypes.Subject, Subject, ClaimValueTypes.String, Issuer)];

        if (providerEmail is not null)
        {
            claims.Add(new Claim(JwtClaimTypes.Email, providerEmail, ClaimValueTypes.String, Issuer));
            claims.Add(new Claim(JwtClaimTypes.EmailVerified, "true", ClaimValueTypes.String, Issuer));
        }

        DefaultHttpContext request = rig.NewRequest();
        await request.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, Schemes.ExternalOidc)), properties);

        return rig.CookieOf(request, IdentityConstants.ExternalScheme);
    }

    private static Task<IActionResult> ConfirmAsync(ExternalLoginModel page, string username)
    {
        page.Input = new ExternalLoginModel.InputModel { Username = username };

        return page.OnPostConfirmationAsync(CancellationToken.None, ReturnUrl);
    }

    private static async Task ShouldHaveCreatedNothingAsync(LocalSchemeRig rig, Invitation invitation, string token)
    {
        (await rig.Users.FindByEmailAsync(InvitedEmail)).Should().BeNull("nothing authorised an account");
        (await Invitations(rig).ValidateAsync(invitation.Uuid, token, [InvitationType.Signup], CancellationToken.None)).Should()
            .NotBeNull("an accept that created nothing spends nothing");
    }

    /// <summary>
    /// The provider scheme's handler. A confirmation never reaches the provider - the challenge is only
    /// built, and the answer is read from the external cookie - so this is never resolved.
    /// </summary>
    // CA1812 is right that nothing constructs this: a scheme must name a handler type, and the type is
    // all that is needed here. The throws make a test that did reach the provider fail loudly.
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Names the provider scheme's handler type; no test contacts the provider.")]
    private sealed class NeverRunHandler : IAuthenticationHandler
    {
        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            throw new NotSupportedException("The provider is never contacted in these tests.");
        }

        public Task<AuthenticateResult> AuthenticateAsync()
        {
            throw new NotSupportedException("The provider is never contacted in these tests.");
        }

        public Task ChallengeAsync(AuthenticationProperties? properties)
        {
            throw new NotSupportedException("The provider is never contacted in these tests.");
        }

        public Task ForbidAsync(AuthenticationProperties? properties)
        {
            throw new NotSupportedException("The provider is never contacted in these tests.");
        }
    }
}
