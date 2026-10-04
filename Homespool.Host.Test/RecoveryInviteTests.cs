using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Mail;
using Homespool.Host.Pages.Account;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The recovery invite: what redeeming one gives back, and the four things it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>A recovery is a takeover primitive pointed at its own owner</b>, so the refusals are what this
/// file is really about: a closed account, an account the invite no longer names, an authenticator
/// cleared when nobody asked, and a second redemption of a spent link. Each was checked by removing
/// the branch and watching exactly one test go red.
/// </para>
/// <para>
/// <b>Driven through the page rather than a service</b>, because the interesting part is the whole
/// redemption - password, provider links, second factor, tokens and the invite - landing together or
/// not at all.
/// </para>
/// </remarks>
public sealed class RecoveryInviteTests : IDisposable
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string NewPassword = "Different-Horse-Battery-Staple-2!"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-recovery-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task RedeemingSetsANewPasswordOnTheAccountTheInviteNames()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().NotBeOfType<PageResult>("the redemption went through");
        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue();
        (await users.CheckPasswordAsync(subject, OldPassword)).Should().BeFalse();

        Invitation spent = await context.Invitations.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        spent.UsedAt.Should().NotBeNull("a recovery link is single-use");

        mail.SentEmails.Should().ContainSingle("the owner is told a recovery happened")
            .Which.email.Should().Be("subject@example.com");
    }

    /// <summary>
    /// The compromised-account reading of a recovery: whoever held the account may have minted
    /// tokens, and those outlive a password by design.
    /// </summary>
    [Fact]
    public async Task RedeemingRevokesTheAccountsApiTokens()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        ApiTokenService tokens = new(context, TimeProvider.System);
        (_, string plaintext) = await tokens.CreateAsync(subject.Id, "laptop", CapabilitySet.Everything, CancellationToken.None);

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, _) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        (await tokens.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// The authenticator is cleared only when the administrator said the person had lost it. This is
    /// the half that hands the whole account to whoever holds the link, so it is not a default.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAuthenticatorGoesOnlyWhenTheInviteSaysSo(bool clearsTwoFactor)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        await users.ResetAuthenticatorKeyAsync(subject);
        (await users.SetTwoFactorEnabledAsync(subject, true)).Succeeded.Should().BeTrue();
        string? keyBefore = await users.GetAuthenticatorKeyAsync(subject);

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, _) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        (await users.GetTwoFactorEnabledAsync(subject)).Should().Be(!clearsTwoFactor);

        if (clearsTwoFactor)
        {
            (await users.GetAuthenticatorKeyAsync(subject)).Should()
                .NotBe(keyBefore, "a flag turned off while the old key still verifies is a second factor somebody can turn back on");
        }
    }

    /// <summary>
    /// A closed account is refused: the sign-in gate would refuse whatever credential the recovery
    /// gave it, so redeeming would look like help and deliver none.
    /// </summary>
    [Fact]
    public async Task ADeactivatedAccountIsNotRecoverable()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        await IdentityTestHarness.MakeAdministratorAsync(provider, users, admin);
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: admin.Id, expiresAt: null, CancellationToken.None);

        await new UserAdministration(context,
                                     new ApiTokenService(context, TimeProvider.System),
                                     provider.GetRequiredService<UserSessionService>(),
                                     provider.GetRequiredService<AttemptLimiter>(),
                                     new UnitOfWork(context),
                                     TimeProvider.System,
                                     new CapturingEmailSender().Notices(),
                                     NullLogger<UserAdministration>.Instance)
            .DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        (await users.CheckPasswordAsync(subject, OldPassword)).Should().BeTrue("nothing was changed");
        mail.SentEmails.Should().BeEmpty();

        Invitation unspent = await context.Invitations.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        unspent.UsedAt.Should().BeNull("a refused redemption does not burn the link");
    }

    /// <summary>
    /// A recovery names an account by id, so an address change between issuing and redeeming cannot
    /// point it at somebody else - which is the whole reason it is not bound to the address.
    /// </summary>
    [Fact]
    public async Task ARecoveryFollowsTheAccountRatherThanTheAddress()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        HSUser bystander = await AddUserAsync(users, "bystander@example.com");

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // The address moves to the other account after the invite was issued.
        (await users.SetEmailAsync(subject, "moved@example.com")).Succeeded.Should().BeTrue();

        (RegisterModel model, _) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue("the invite named this account");
        (await users.CheckPasswordAsync(bystander, NewPassword)).Should().BeFalse();
        (await users.CheckPasswordAsync(bystander, OldPassword)).Should().BeTrue();
    }

    /// <summary>
    /// A recovery whose account cannot be found is a dead link, not a signup: the page says so on
    /// the way in, and a post creates nothing and leaves the invite unspent.
    /// </summary>
    /// <remarks>
    /// The username is filled in so that the signup branch, were it reached, would succeed - an
    /// account appearing at the address is what this asserts against, and a form the signup branch
    /// refused for want of a name would pass for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task ARecoveryForAnAccountThatIsGoneCreatesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            9999, "gone@example.com", clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel viewing, _) = NewModel(context, users, provider, invitations, invite, token);
        (RegisterModel posting, _) = NewModel(context, users, provider, invitations, invite, token);
        posting.Input.Username = "newcomer";

        // Act
        await viewing.OnGetAsync(returnUrl: null, CancellationToken.None);
        IActionResult result = await posting.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        viewing.InviteValid.Should().BeFalse("there is nothing left for the link to recover");

        result.Should().BeOfType<PageResult>();
        posting.InviteValid.Should().BeFalse();
        (await users.FindByEmailAsync("gone@example.com")).Should().BeNull("a recovery is not a signup");

        Invitation stored = await context.Invitations.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        stored.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task ASpentRecoveryCannotBeRedeemedAgain()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel first, _) = NewModel(context, users, provider, invitations, invite, token);
        await first.OnPostAsync(returnUrl: null, CancellationToken.None);

        (RegisterModel second, _) = NewModel(context, users, provider, invitations, invite, token, password: "Third-Horse-Battery-Staple-3!"); // betterleaks:allow

        // Act
        IActionResult result = await second.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        second.InviteValid.Should().BeFalse();
        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue("the first redemption stands and the second did nothing");
    }

    /// <summary>
    /// The notice is for the account's owner, and the browser redeeming the link is the one that may
    /// not be theirs - so it is written in the account's language, not the request's.
    /// </summary>
    [Fact]
    public async Task TheOwnersNoticeIsWrittenInTheAccountsLanguage()
    {
        // Arrange
        using RequestCulture request = RequestCulture.English();
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com", language: "da");
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        mail.SentEmails.Should().ContainSingle().Which.subject.Should().Be("Din Homespool-adgangskode er nulstillet");
    }

    /// <summary>
    /// A recovered account whose address was never confirmed is held for confirmation, and that mail
    /// goes to the account too - so it is in the account's language, like the notice beside it.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedAccountsConfirmationMailIsInTheAccountsLanguage()
    {
        // Arrange
        using RequestCulture request = RequestCulture.English();
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com", language: "da", confirmed: false);
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("RegisterConfirmation");
        mail.SentEmails.Select(sent => sent.subject).Should().Equal(
            "Din Homespool-adgangskode er nulstillet",
            "Bekræft din e-mailadresse");
    }

    /// <summary>
    /// The form checks the length; the character rules are Identity's, and a password they refuse is
    /// refused as a form error with nothing changed - the old password still works, the link is
    /// still live, and the owner hears nothing, because nothing happened.
    /// </summary>
    [Fact]
    public async Task APasswordIdentityRefusesChangesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Long enough for the form, and nothing Identity's composition rules accept.
        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token, password: "correcthorsebatterystaple"); // betterleaks:allow

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.InviteValid.Should().BeTrue("the link was fine; the password was not");
        model.ModelState[string.Empty]!.Errors.Should().NotBeEmpty("Identity's refusal is what the form shows");

        (await users.CheckPasswordAsync(subject, OldPassword)).Should().BeTrue("nothing was changed");
        mail.SentEmails.Should().BeEmpty();

        Invitation unspent = await context.Invitations.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        unspent.UsedAt.Should().BeNull("a refused redemption does not burn the link");
    }

    /// <summary>
    /// A password beside a live provider link is the parallel credential the account rules refuse,
    /// so recovering a provider-only account swaps the link for the password. An account that already
    /// had a password keeps whatever it holds: the recovery is not a tidy-up.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAnAccountWithNoPasswordLosesItsProviderLinks(bool hadPassword)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com", withPassword: hadPassword);
        (await users.AddLoginAsync(subject, new UserLoginInfo("oidc", ExternalSignIn.ProviderKey("https://issuer.example.net", "42"), "Dex")))
            .Succeeded.Should().BeTrue();

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, _) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LocalRedirectResult>("the redemption went through");
        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue();
        (await users.GetLoginsAsync(subject)).Should().HaveCount(hadPassword ? 1 : 0);
    }

    /// <summary>
    /// The whole redemption lands or none of it does: a write refused late in the transaction takes
    /// the password already written with it, leaves the link live, and tells the owner nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusal comes from a trigger on the invite's spending, the last write in the transaction,
    /// so every earlier write has happened when it fires. Take the catch out of <c>RecoverAsync</c>
    /// and this test fails with the <c>DbUpdateException</c> the page would otherwise answer 500 with.
    /// </para>
    /// <para>
    /// Asserted against fresh reads: the tracked instances still carry the rolled-back values in
    /// memory, so asserting against them would pass whether or not the database kept anything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWriteRefusedInsideTheTransactionRollsBackThePassword()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"RefuseSpending\" BEFORE UPDATE OF \"UsedAt\" ON \"Invitations\" " +
            "BEGIN SELECT RAISE(ABORT, 'the test refuses this write'); END;",
            TestContext.Current.CancellationToken);

        FakeLogger<RegisterModel> logger = new();
        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token, logger: logger);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState[string.Empty]!.Errors.Should().ContainSingle(e => e.ErrorMessage.Contains("try again"));

        HSUser stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == subject.Id, TestContext.Current.CancellationToken);
        (await users.CheckPasswordAsync(stored, OldPassword)).Should().BeTrue("the password written before the refusal was rolled back with it");

        Invitation unspent = await context.Invitations.AsNoTracking().SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        unspent.UsedAt.Should().BeNull();

        mail.SentEmails.Should().BeEmpty("nothing changed, so there is nothing to tell the owner");

        FakeLogRecord error = logger.Collector.GetSnapshot().Should().ContainSingle(record => record.Level == LogLevel.Error).Subject;
        error.StructuredState.Should().Contain(property => property.Key == "InviteUuid" && property.Value == invite.Uuid.ToString());
    }

    /// <summary>
    /// The owner's notice is outside the transaction and best effort: a mail that cannot be sent is
    /// logged against the account, and the recovery it reports stands.
    /// </summary>
    [Fact]
    public async Task ANoticeThatCannotBeSentDoesNotUndoTheRecovery()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        InvitationService invitations = NewInvitationService(context);

        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        FakeLogger<RegisterModel> logger = new();
        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token, logger: logger);
        mail.Result = EmailSendResult.Failed;

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LocalRedirectResult>("the notice is not part of the write");
        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue();

        FakeLogRecord warning = logger.Collector.GetSnapshot().Should()
            .ContainSingle(record => record.Level == LogLevel.Warning && record.Message.Contains("could not be sent")).Subject;
        warning.StructuredState.Should().Contain(property => property.Key == "UserId" && property.Value == subject.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A recovery is a proved password, and what follows a proved password anywhere else follows it
    /// here: a locked-out account gets its password back and is sent to the lockout page, not signed
    /// in. The lockout lifts on its own, and the new password is what works afterwards.
    /// </summary>
    [Fact]
    public async Task ALockedOutAccountIsRecoveredButNotSignedIn()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        (await users.SetLockoutEndDateAsync(subject, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded.Should().BeTrue();
        (await users.IsLockedOutAsync(subject)).Should().BeTrue("test setup");

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        (RegisterModel model, CapturingEmailSender mail) = NewModel(context, users, provider, invitations, invite, token);

        // Act
        IActionResult result = await model.OnPostAsync(returnUrl: null, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("./Lockout");
        model.HttpContext.Response.Headers.Should().NotContainKey("Set-Cookie", "a lockout is not something a recovery gets around");

        (await users.CheckPasswordAsync(subject, NewPassword)).Should().BeTrue("the recovery itself stands");
        mail.SentEmails.Should().ContainSingle("the owner is told either way");

        Invitation spent = await context.Invitations.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        spent.UsedAt.Should().NotBeNull();
    }

    private static InvitationService NewInvitationService(HomespoolDbContext context)
    {
        return new(context, new TokenService(), TestOptions.Snapshot(new InvitationOptions()), TimeProvider.System);
    }

    private static (RegisterModel model, CapturingEmailSender mail) NewModel(HomespoolDbContext context,
                                                                             UserManager<HSUser> users,
                                                                             IServiceProvider provider,
                                                                             InvitationService invitations,
                                                                             Invitation invite,
                                                                             string plaintextToken,
                                                                             string password = NewPassword,
                                                                             ILogger<RegisterModel>? logger = null)
    {
        DefaultHttpContext httpContext = new() { RequestServices = provider };
        CapturingEmailSender mail = new();

        RegisterModel model = new(
            users,
            provider.GetRequiredService<IUserStore<HSUser>>(),
            provider.GetRequiredService<LocalSignIn>(),
            provider.GetRequiredService<LocalSignInRules>(),
            provider.GetRequiredService<ExternalSignIn>(),
            logger ?? NullLogger<RegisterModel>.Instance,
            mail,
            new AccountConfirmationPolicy(Options.Create(new SmtpOptions { Host = string.Empty })),
            invitations,
            new TeamService(context),
            new UnitOfWork(context),
            new ApiTokenService(context, TimeProvider.System),
            TimeProvider.System,
            TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            InviteUuid = invite.Uuid,
            Code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Text.Encoding.UTF8.GetBytes(plaintextToken)),
            Input = new RegisterModel.InputModel
            {
                Password = password,
                ConfirmPassword = password,
            },
        };

        return (model, mail);
    }

    /// <summary>
    /// An account holding <see cref="OldPassword"/>, or with <paramref name="withPassword"/> off one
    /// that never had a local credential - what a provider-only account looks like to the store.
    /// </summary>
    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users,
                                                   string email,
                                                   string? language = null,
                                                   bool confirmed = true,
                                                   bool withPassword = true)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = confirmed,
            Language = language,
        };

        IdentityResult created = withPassword ? await users.CreateAsync(user, OldPassword) : await users.CreateAsync(user);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

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
