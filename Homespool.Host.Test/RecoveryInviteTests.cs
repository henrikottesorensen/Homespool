using System;
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
using Microsoft.Extensions.Logging.Abstractions;
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
        ApiTokenService tokens = new(context);
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
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        InvitationService invitations = NewInvitationService(context);
        (Invitation invite, string token) = await invitations.CreateRecoveryAsync(
            subject.Id, subject.Email!, clearsTwoFactor: false, invitedBy: admin.Id, expiresAt: null, CancellationToken.None);

        await new UserAdministration(context,
                                     new ApiTokenService(context),
                                     provider.GetRequiredService<AttemptLimiter>(),
                                     new UnitOfWork(context),
                                     TimeProvider.System,
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

    private static InvitationService NewInvitationService(HomespoolDbContext context)
    {
        return new(context, new TokenService(), TestOptions.Snapshot(new InvitationOptions()));
    }

    private static (RegisterModel model, CapturingEmailSender mail) NewModel(HomespoolDbContext context,
                                                                             UserManager<HSUser> users,
                                                                             IServiceProvider provider,
                                                                             InvitationService invitations,
                                                                             Invitation invite,
                                                                             string plaintextToken,
                                                                             string password = NewPassword)
    {
        DefaultHttpContext httpContext = new() { RequestServices = provider };
        CapturingEmailSender mail = new();

        RegisterModel model = new(
            users,
            provider.GetRequiredService<IUserStore<HSUser>>(),
            provider.GetRequiredService<LocalSignIn>(),
            provider.GetRequiredService<LocalSignInRules>(),
            provider.GetRequiredService<ExternalSignIn>(),
            NullLogger<RegisterModel>.Instance,
            mail,
            new AccountConfirmationPolicy(Options.Create(new SmtpOptions { Host = string.Empty })),
            invitations,
            new TeamService(context),
            new UnitOfWork(context),
            new ApiTokenService(context),
            TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Url = IdentityTestHarness.NewUrlHelper(httpContext),
            InviteId = invite.Id,
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

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, OldPassword);
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
