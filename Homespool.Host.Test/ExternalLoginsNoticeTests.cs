using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Linking a provider, removing one, and swapping the last one for a password each mail the owner,
/// and an attempt that changed nothing mails nobody.
/// </summary>
/// <remarks>
/// The recent proof these handlers want is the filter's to demand and is not part of the model; the
/// provider is faked at the store, and <see cref="ExternalRoundTripTests"/> covers the answer itself.
/// </remarks>
public sealed class ExternalLoginsNoticeTests : IDisposable
{
    private const string Provider = "oidc";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-loginnotice-{Guid.NewGuid():N}.db");
    private readonly CapturingEmailSender _mail = new();

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
    public async Task LinkingMailsTheOwner()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        string answer = await AnswerAsync(rig, "new-subject", ExternalRoundTrip.Link, owner);
        ExternalLoginsModel page = await PageAsync(rig, owner, answer);

        // Act
        await page.OnGetLinkLoginCallbackAsync();

        // Assert
        (await rig.Users.FindByLoginAsync(Provider, "new-subject"))!.Id.Should().Be(owner.Id);
        (string email, string subject, string body) sent = _mail.SentEmails.Should().ContainSingle().Subject;
        sent.email.Should().Be("owner@example.com");
        sent.subject.Should().Be("A sign-in provider was linked to your Homespool account");
        sent.body.Should().StartWith(Provider + " was just linked");
    }

    [Fact]
    public async Task ALinkThatIsRefusedMailsNobody()
    {
        // Arrange - an answer to somebody else's round trip
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        string answer = await AnswerAsync(rig, "new-subject", ExternalRoundTrip.Link, other);
        ExternalLoginsModel page = await PageAsync(rig, owner, answer);

        // Act
        await page.OnGetLinkLoginCallbackAsync();

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().BeEmpty();
        _mail.SentEmails.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAProviderMailsTheOwner()
    {
        // Arrange - an account with a password, so removal is just removal
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(owner, new UserLoginInfo(Provider, "subject", Provider))).Succeeded.Should().BeTrue();
        ExternalLoginsModel page = await PageAsync(rig, owner);

        // Act
        await page.OnPostRemoveLoginAsync(Provider, "subject", CancellationToken.None);

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().BeEmpty();
        _mail.SentEmails.Should().ContainSingle().Which.subject.Should().Be("A sign-in provider was removed from your Homespool account");
    }

    [Fact]
    public async Task RemovingALoginTheAccountDoesNotHoldMailsNobody()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(owner, new UserLoginInfo(Provider, "subject", Provider))).Succeeded.Should().BeTrue();
        ExternalLoginsModel page = await PageAsync(rig, owner);

        // Act
        await page.OnPostRemoveLoginAsync("elsewhere", "somebody", CancellationToken.None);

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().ContainSingle();
        _mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>
    /// While the last change's cooldown runs, the next removal is refused before anything is written, so
    /// a loop cannot mail the owner at request rate.
    /// </summary>
    [Fact]
    public async Task ARemovalPastTheLimitIsRefusedAndMailsNobody()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(owner, new UserLoginInfo(Provider, "subject", Provider))).Succeeded.Should().BeTrue();
        await SpendTheChangeAsync(rig, owner);
        ExternalLoginsModel page = await PageAsync(rig, owner);

        // Act
        await page.OnPostRemoveLoginAsync(Provider, "subject", CancellationToken.None);

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().ContainSingle("nothing was removed");
        _mail.SentEmails.Should().BeEmpty();
        page.StatusMessage.Should().Be("Too many changes to how you sign in. Try again in a few minutes.");
    }

    [Fact]
    public async Task ALinkPastTheLimitIsRefusedAndMailsNobody()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        string answer = await AnswerAsync(rig, "new-subject", ExternalRoundTrip.Link, owner);
        await SpendTheChangeAsync(rig, owner);
        ExternalLoginsModel page = await PageAsync(rig, owner, answer);

        // Act
        await page.OnGetLinkLoginCallbackAsync();

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().BeEmpty();
        _mail.SentEmails.Should().BeEmpty();
    }

    [Fact]
    public async Task ASwapPastTheLimitIsRefusedAndSetsNoPassword()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await ProviderOnlyAccountAsync(rig);
        await SpendTheChangeAsync(rig, owner);
        ExternalLoginsModel page = await PageAsync(rig, owner);
        page.Input = new ExternalLoginsModel.InputModel { NewPassword = LocalSchemeRig.Password, ConfirmPassword = LocalSchemeRig.Password };

        // Act
        await page.OnPostRemoveLoginAsync(Provider, "subject", CancellationToken.None);

        // Assert
        (await StoredPasswordHashAsync(rig, owner)).Should().BeNull();
        (await rig.Users.GetLoginsAsync(owner)).Should().ContainSingle();
        _mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>The takeover the page's remarks describe, told to the owner in its own words.</summary>
    [Fact]
    public async Task SwappingTheLastProviderForAPasswordMailsTheOwner()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await ProviderOnlyAccountAsync(rig);
        ExternalLoginsModel page = await PageAsync(rig, owner);
        page.Input = new ExternalLoginsModel.InputModel { NewPassword = LocalSchemeRig.Password, ConfirmPassword = LocalSchemeRig.Password };

        // Act
        await page.OnPostRemoveLoginAsync(Provider, "subject", CancellationToken.None);

        // Assert
        (await StoredPasswordHashAsync(rig, owner)).Should().NotBeNull();
        _mail.SentEmails.Should().ContainSingle().Which.subject.Should().Be("Your Homespool account now signs in with a password");
    }

    [Fact]
    public async Task ASwapThatIsRefusedMailsNobody()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await ProviderOnlyAccountAsync(rig);
        ExternalLoginsModel page = await PageAsync(rig, owner);
        page.Input = new ExternalLoginsModel.InputModel { NewPassword = "abc", ConfirmPassword = "abc" };

        // Act
        await page.OnPostRemoveLoginAsync(Provider, "subject", CancellationToken.None);

        // Assert
        (await rig.Users.GetLoginsAsync(owner)).Should().ContainSingle();
        _mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>
    /// A swap naming a login the account does not hold sets no password: the removal fails, and the
    /// transaction takes the password with it. Otherwise the caller's password would sit beside the
    /// owner's provider, which the owner would never notice.
    /// </summary>
    [Fact]
    public async Task ASwapNamingALoginTheAccountDoesNotHoldSetsNoPasswordAndMailsNobody()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await ProviderOnlyAccountAsync(rig);
        ExternalLoginsModel page = await PageAsync(rig, owner);
        page.Input = new ExternalLoginsModel.InputModel { NewPassword = LocalSchemeRig.Password, ConfirmPassword = LocalSchemeRig.Password };

        // Act
        await page.OnPostRemoveLoginAsync("elsewhere", "somebody", CancellationToken.None);

        // Assert
        (await StoredPasswordHashAsync(rig, owner)).Should().BeNull("the rollback took the password with it");
        (await rig.Users.GetLoginsAsync(owner)).Should().ContainSingle();
        page.ModelState.IsValid.Should().BeFalse("the refusal is shown on the form");
        _mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>
    /// The password hash as the database holds it. The tracked entity keeps a hash a rolled-back
    /// transaction never saved, so <c>HasPasswordAsync</c> on it would answer for the attempt.
    /// </summary>
    private static async Task<string?> StoredPasswordHashAsync(LocalSchemeRig rig, HSUser user)
    {
        return (await rig.Context.Users.AsNoTracking()
                         .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken)).PasswordHash;
    }

    /// <summary>Starts <paramref name="user"/>'s change cooldown, as a change a moment ago would have.</summary>
    private static async Task SpendTheChangeAsync(LocalSchemeRig rig, HSUser user)
    {
        CredentialChangeLimit limit = new(rig.NewRequest().RequestServices.GetRequiredService<AttemptLimiter>(),
                                          TimeProvider.System,
                                          NullLogger<CredentialChangeLimit>.Instance);

        (await limit.TryStartAsync(user.Id, CancellationToken.None)).Should().BeTrue();
    }

    private static async Task<HSUser> ProviderOnlyAccountAsync(LocalSchemeRig rig)
    {
        HSUser user = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();
        (await rig.Users.AddLoginAsync(user, new UserLoginInfo(Provider, "subject", Provider))).Succeeded.Should().BeTrue();

        return user;
    }

    /// <summary>The external cookie a provider's answer leaves, bound as the challenge for <paramref name="roundTrip"/> bound it.</summary>
    private static async Task<string> AnswerAsync(LocalSchemeRig rig, string subject, ExternalRoundTrip roundTrip, HSUser expectedAccount)
    {
        DefaultHttpContext request = rig.NewRequest();
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(JwtClaimTypes.Subject, subject)], Provider));
        AuthenticationProperties properties = ExternalSignIn.ChallengeProperties(Provider, "/back", roundTrip, expectedAccount.Id.ToString());

        await request.SignInAsync(IdentityConstants.ExternalScheme, principal, properties);

        return rig.CookieOf(request, IdentityConstants.ExternalScheme);
    }

    /// <summary>The page over a request signed in as <paramref name="user"/>, carrying <paramref name="cookies"/> as well.</summary>
    private async Task<ExternalLoginsModel> PageAsync(LocalSchemeRig rig, HSUser user, params string[] cookies)
    {
        DefaultHttpContext request = rig.NewRequest([await rig.SessionCookieAsync(user), .. cookies]);
        request.Response.Body = new MemoryStream();
        request.User = (await request.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Principal!;

        IServiceProvider services = request.RequestServices;

        return new ExternalLoginsModel(rig.Users,
                                       services.GetRequiredService<LocalSignIn>(),
                                       services.GetRequiredService<ExternalSignIn>(),
                                       services.GetRequiredService<RecentProof>(),
                                       new UnitOfWork(services.GetRequiredService<HomespoolDbContext>()),
                                       _mail.Notices(),
                                       new CredentialChangeLimit(services.GetRequiredService<AttemptLimiter>(), TimeProvider.System, NullLogger<CredentialChangeLimit>.Instance),
                                       NullLogger<ExternalLoginsModel>.Instance,
                                       TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Url = IdentityTestHarness.NewUrlHelper(request),
        };
    }
}
