using System;
using System.Buffers.Text;
using System.Collections.Generic;
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

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Admin.Users;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The administrator's user screens: the roster reads what is there, and every act on the detail
/// page takes the administrator's own password first.
/// </summary>
/// <remarks>
/// <b>The step-up is the assertion that matters here</b>, because the service's own guards are
/// tested next door: a page that acted on a wrong password would pass every "deactivation
/// deactivates" test ever written. Checked by mutation - dropping the proof leaves exactly the
/// wrong-password test red.
/// </remarks>
public sealed class AdminUsersPageTests : IDisposable
{
    private const string Password = "Correct horse battery staple 1"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-adminusers-{Guid.NewGuid():N}.db");

    private readonly List<IServiceScope> _scopes = [];

    /// <summary>What the recovery invite's mail went to, when a test issued one.</summary>
    private CapturingEmailSender Mail { get; } = new();

    private static InvitationService NewInvitationService(HomespoolDbContext context)
    {
        return new(context, new TokenService(), TestOptions.Snapshot(new InvitationOptions()));
    }

    public void Dispose()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TheRosterListsEveryAccountWithWhatItSignsInWith()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser alice = await AddUserAsync(users, "alice@example.com");
        HSUser bob = await AddUserAsync(users, "bob@example.com");
        await SeedPasskeyAsync(users, bob, "phone");
        await new ApiTokenService(context).CreateAsync(bob.Id, "laptop", [Capability.Print], CancellationToken.None);
        await Administration(context, provider).DeactivateAsync(alice.Id, bob.Id, CancellationToken.None);

        IndexModel model = new(context, TimeProvider.System);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Rows.Should().HaveCount(2);
        model.Rows.Select(row => row.UserName).Should().ContainInOrder("alice", "bob");

        IndexModel.Row bobRow = model.Rows.Single(row => row.UserName == "bob");
        bobRow.HasPassword.Should().BeTrue();
        bobRow.Passkeys.Should().Be(1);
        bobRow.Tokens.Should().Be(0, "deactivating took them");
        bobRow.Deactivated.Should().BeTrue();
        model.Rows.Single(row => row.UserName == "alice").Deactivated.Should().BeFalse();
    }

    [Fact]
    public async Task TheDetailPageShowsTheAccountsCredentialsAndTeams()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        await SeedPasskeyAsync(users, subject, "phone");
        await new ApiTokenService(context).CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        await new TeamService(context).AddDefaultTeamAsync(subject.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnGetAsync(subject.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.UserName.Should().Be("subject");
        model.HasPassword.Should().BeTrue();
        model.Passkeys.Should().ContainSingle().Which.Name.Should().Be("phone");
        model.Tokens.Should().ContainSingle().Which.Name.Should().Be("laptop");
        model.Teams.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
        model.IsSelf.Should().BeFalse();
    }

    [Fact]
    public async Task TheDetailPageIsNotFoundForAnAccountThatIsNotThere()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnGetAsync(9999, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeactivatingClosesTheAccountAndSaysWhatItTook()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        await new ApiTokenService(context).CreateAsync(subject.Id, "laptop", [Capability.Print], CancellationToken.None);
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostDeactivateAsync(subject.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("Account deactivated, and its one API token revoked.");
        (await Reload(context, subject.Id)).DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeactivatingYourOwnAccountIsRefusedAndSaysWhy()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostDeactivateAsync(admin.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().StartWith("You cannot deactivate your own account");
        (await Reload(context, admin.Id)).DeactivatedAt.Should().BeNull();
    }

    /// <summary>
    /// An administrator recovering themselves would clear their own second factor on their password
    /// alone - which is exactly what Disable2fa refuses by demanding a live code.
    /// </summary>
    [Fact]
    public async Task RecoveringYourOwnAccountIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        (DetailModel model, _) = NewDetail(context, provider, users, admin);
        model.ClearAuthenticator = true;

        // Act
        IActionResult result = await model.OnPostRecoverAsync(admin.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.StatusMessage.Should().StartWith("You cannot send yourself a recovery link");
        model.RecoveryLink.Should().BeNull();
        (await context.Invitations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        Mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>
    /// A closed account gets no recovery link either: it could not be redeemed, and an invite that
    /// cannot work is worse than a refusal because it looks like help.
    /// </summary>
    [Fact]
    public async Task ADeactivatedAccountIsRefusedARecovery()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        await Administration(context, provider).DeactivateAsync(admin.Id, subject.Id, CancellationToken.None);

        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostRecoverAsync(subject.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.StatusMessage.Should().StartWith("That account is deactivated");
        model.RecoveryLink.Should().BeNull();
        (await context.Invitations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>The happy path: a link, rendered once, and a mail to the account.</summary>
    [Fact]
    public async Task IssuingARecoveryRendersTheLinkAndMailsIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostRecoverAsync(subject.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.RecoveryLink.Should().Contain("/Account/Register");
        Mail.SentEmails.Should().ContainSingle().Which.email.Should().Be("subject@example.com");

        Invitation issued = await context.Invitations.SingleAsync(TestContext.Current.CancellationToken);
        issued.RecoversUserId.Should().Be(subject.Id);
        issued.ClearsTwoFactor.Should().BeFalse("the administrator did not tick it");
    }

    [Fact]
    public async Task RevokingAPasskeyRemovesThatOneAndNoOther()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        UserPasskeyInfo phone = await SeedPasskeyAsync(users, subject, "phone");
        UserPasskeyInfo laptop = await SeedPasskeyAsync(users, subject, "laptop");
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostRevokePasskeyAsync(
            subject.Id, Base64Url.EncodeToString(phone.CredentialId), CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("Passkey revoked.");
        (await users.GetPasskeysAsync(subject)).Should().ContainSingle()
                                               .Which.CredentialId.Should().Equal(laptop.CredentialId);
    }

    [Fact]
    public async Task RevokingAPasskeyThatIsGoneSaysSoAndAMalformedOneIsNotFound()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");
        (DetailModel gone, _) = NewDetail(context, provider, users, admin);
        (DetailModel malformed, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult goneResult = await gone.OnPostRevokePasskeyAsync(
            subject.Id, Base64Url.EncodeToString(Guid.NewGuid().ToByteArray()), CancellationToken.None);
        IActionResult malformedResult = await malformed.OnPostRevokePasskeyAsync(
            subject.Id, "not base64url!", CancellationToken.None);

        // Assert
        goneResult.Should().BeOfType<RedirectToPageResult>();
        gone.StatusMessage.Should().Be("That passkey was already gone.");
        malformedResult.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ClearingALockoutLetsTheAccountSignInAgain()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        HSUser subject = await AddUserAsync(users, "subject@example.com");

        for (int attempt = 0; attempt < 9; attempt++)
        {
            await users.AccessFailedAsync(subject);
        }

        (await users.IsLockedOutAsync(subject)).Should().BeTrue();
        (DetailModel model, _) = NewDetail(context, provider, users, admin);

        // Act
        IActionResult result = await model.OnPostClearLockoutAsync(subject.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().StartWith("Lockout cleared.");
        (await users.IsLockedOutAsync(subject)).Should().BeFalse();
    }

    private static UserAdministration Administration(HomespoolDbContext context, IServiceProvider provider)
    {
        return new UserAdministration(context,
                                      new ApiTokenService(context),
                                      provider.GetRequiredService<AttemptLimiter>(),
                                      new UnitOfWork(context),
                                      TimeProvider.System,
                                      NullLogger<UserAdministration>.Instance);
    }

    private static async Task<HSUser> Reload(HomespoolDbContext context, long id)
    {
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    private static async Task<UserPasskeyInfo> SeedPasskeyAsync(UserManager<HSUser> users, HSUser user, string name)
    {
        UserPasskeyInfo passkey = new(
            credentialId: Guid.NewGuid().ToByteArray(),
            publicKey: [1, 2, 3],
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: null,
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: [],
            clientDataJson: [])
        {
            Name = name,
        };

        (await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();

        return passkey;
    }

    /// <summary>
    /// The detail page over a request from <paramref name="administrator"/>, signed in.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here proves anything, and that is the page's shape now</b>: what stands between a
    /// session and these acts is the elevation the request had to carry to reach the page at all,
    /// which <c>AdminElevationTests</c> and <c>AdminChallengePageTests</c> cover. A scope per
    /// request, as a real request has.
    /// </remarks>
    private (DetailModel model, DefaultHttpContext request) NewDetail(HomespoolDbContext context,
                                                                      IServiceProvider provider,
                                                                      UserManager<HSUser> users,
                                                                      HSUser administrator)
    {
        IServiceScope scope = provider.CreateScope();
        _scopes.Add(scope);

        DefaultHttpContext request = new() { RequestServices = scope.ServiceProvider };
        request.Request.Scheme = "https";
        request.Request.Host = new HostString("homespool.test");
        request.Response.Body = new MemoryStream();

        IdentityTestHarness.SignInAsPrincipal(request, administrator);

        DetailModel model = new(context,
                                users,
                                Administration(context, provider),
                                new TeamService(context),
                                NewInvitationService(context),
                                Mail,
                                new CapabilityText(TestLocaliser.Shared()),
                                TestLocaliser.Shared(),
                                TimeProvider.System,
                                NullLogger<DetailModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Url = IdentityTestHarness.NewUrlHelper(request),
        };

        return (model, request);
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
