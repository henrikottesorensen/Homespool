using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The Manage page's three verbs - add, rename, remove - driven against a real Identity stack with
/// <see cref="FakeAuthenticator"/> as the browser's half of a registration.
/// </summary>
/// <remarks>
/// <b>A registration is two requests, like a sign-in</b>, so the page model is built twice: once to
/// begin, once to finish, with the ceremony cookie carried across by hand. The refusals were checked
/// by mutation: the name check, the ceremony-operation check and the account check each fail their
/// test when removed.
/// </remarks>
public sealed class PasskeysPageTests : IDisposable
{
    private const string RelyingPartyId = "homespool.test";
    private const string PagePath = "/Account/Manage/Passkeys";
    private const string Password = "Correct horse battery staple 1"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-passkeypage-{Guid.NewGuid():N}.db");

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

    // ---------- adding ----------
    [Fact]
    public async Task RegisteringStoresThePasskeyUnderTheNameGiven()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        using FakeAuthenticator authenticator = new() { BackedUp = true };

        (PasskeysModel begin, DefaultHttpContext beginRequest) = rig.NewModel(user);
        ContentResult options = (await begin.OnPostBeginRegistrationAsync(CancellationToken.None)).Should().BeOfType<ContentResult>().Subject;

        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(beginRequest));
        register.Input.Name = "MacBook";

        // Act
        IActionResult result = await register.OnPostRegisterAsync(authenticator.Attest(options.Content!));
        IList<UserPasskeyInfo> stored = await rig.Users.GetPasskeysAsync(user);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        options.ContentType.Should().StartWith("application/json");
        stored.Should().ContainSingle();
        stored[0].Name.Should().Be("MacBook");
        stored[0].CredentialId.Should().Equal(authenticator.CredentialId);
        stored[0].IsBackupEligible.Should().BeTrue("the flags the authenticator reported are what the page shows later");
        register.StatusMessage.Should().Be("Passkey added.");
    }

    [Fact]
    public async Task AnEmptyNameGetsADatedDefault()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        using FakeAuthenticator authenticator = new();

        (PasskeysModel begin, DefaultHttpContext beginRequest) = rig.NewModel(user);
        ContentResult options = (await begin.OnPostBeginRegistrationAsync(CancellationToken.None)).Should().BeOfType<ContentResult>().Subject;
        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(beginRequest));
        register.Input.Name = "   ";

        // Act
        await register.OnPostRegisterAsync(authenticator.Attest(options.Content!));
        IList<UserPasskeyInfo> stored = await rig.Users.GetPasskeysAsync(user);

        // Assert
        stored.Should().ContainSingle().Which.Name.Should().StartWith("Passkey added ");
    }

    /// <summary>
    /// A name the form refuses stops the registration before the ceremony is consumed, so the browser's
    /// answer is not spent on a request that was going to fail anyway.
    /// </summary>
    [Fact]
    public async Task AnInvalidNameIsRefusedBeforeTheCeremony()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        using FakeAuthenticator authenticator = new();

        (PasskeysModel begin, DefaultHttpContext beginRequest) = rig.NewModel(user);
        ContentResult options = (await begin.OnPostBeginRegistrationAsync(CancellationToken.None)).Should().BeOfType<ContentResult>().Subject;
        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(beginRequest));
        register.Input.Name = new string('x', PasskeysModel.NameMaxLength + 1);
        register.ModelState.AddModelError("Input.Name", "too long");

        // Act
        IActionResult result = await register.OnPostRegisterAsync(authenticator.Attest(options.Content!));

        // Assert
        result.Should().BeOfType<PageResult>();
        (await rig.Users.GetPasskeysAsync(user)).Should().BeEmpty();
    }

    [Fact]
    public async Task RegistrationIsWithheldOnAnUncoveredHost()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        (PasskeysModel model, _) = rig.NewModel(user, host: "homespool.lan");

        // Act
        IActionResult page = await model.OnGetAsync();
        IActionResult begin = await model.OnPostBeginRegistrationAsync(CancellationToken.None);

        // Assert
        page.Should().BeOfType<PageResult>();
        model.PasskeysAvailable.Should().BeFalse();
        model.ServerDomain.Should().Be(RelyingPartyId, "the page says which address to come back by");
        begin.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task AnAttestationForAnotherCeremonyIsRefused()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        using FakeAuthenticator authenticator = new();

        (PasskeysModel first, DefaultHttpContext firstRequest) = rig.NewModel(user);
        await first.OnPostBeginRegistrationAsync(CancellationToken.None);
        (PasskeysModel second, _) = rig.NewModel(user);
        ContentResult secondOptions = (await second.OnPostBeginRegistrationAsync(CancellationToken.None)).Should().BeOfType<ContentResult>().Subject;

        // The second ceremony's answer, with the first ceremony's cookie.
        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(firstRequest));

        // Act
        IActionResult result = await register.OnPostRegisterAsync(authenticator.Attest(secondOptions.Content!));

        // Assert
        result.Should().BeOfType<PageResult>();
        register.ModelState.IsValid.Should().BeFalse();
        (await rig.Users.GetPasskeysAsync(user)).Should().BeEmpty();
    }

    /// <summary>
    /// The ceremony cookie carries which operation it was started for. A sign-in ceremony's state,
    /// even one valid for a registration in every other respect, is refused as a registration.
    /// </summary>
    [Fact]
    public async Task ASignInCeremonyCannotBeAnsweredAsARegistration()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        using FakeAuthenticator authenticator = new();

        // Real creation options from the engine, filed under the wrong operation.
        (_, DefaultHttpContext request) = rig.NewModel(user);
        PasskeyCreationOptionsResult creation = await rig.Engine.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = user.Id.ToString(CultureInfo.InvariantCulture), Name = user.UserName!, DisplayName = user.UserName! },
            request);
        rig.Ceremonies.Begin(request, PasskeyCeremonies.Assertion, creation.AttestationState!);

        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(request));

        // Act
        IActionResult result = await register.OnPostRegisterAsync(authenticator.Attest(creation.CreationOptionsJson));

        // Assert
        result.Should().BeOfType<PageResult>();
        (await rig.Users.GetPasskeysAsync(user)).Should().BeEmpty("a ceremony is spent for the operation it was started for");
    }

    // ---------- the password gate ----------
    [Fact]
    public async Task AWrongPasswordRefusesToStartACeremony()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        (PasskeysModel model, DefaultHttpContext request) = rig.NewModel(user, password: "not it"); // betterleaks:allow

        // Act
        IActionResult result = await model.OnPostBeginRegistrationAsync(CancellationToken.None);

        // Assert
        JsonResult refusal = result.Should().BeOfType<JsonResult>().Subject;
        refusal.StatusCode.Should().Be(401);
        request.Response.Headers.SetCookie.ToString().Should().BeEmpty("no ceremony starts on a wrong password");
    }

    [Fact]
    public async Task RepeatedWrongPasswordsBackOff()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        int threshold = new AttemptLimitOptions().MaxFailedAttempts;

        for (int i = 0; i <= threshold; i += 1)
        {
            (PasskeysModel wrong, _) = rig.NewModel(user, password: "not it"); // betterleaks:allow
            await wrong.OnPostBeginRegistrationAsync(CancellationToken.None);
        }

        // Even the right password is refused while backed off, and the refusal says so.
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult result = await model.OnPostBeginRegistrationAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<JsonResult>().Which.StatusCode.Should().Be(429);
        (await rig.Limiter.RemainingLockoutAsync(user.Id, LimitedAction.AddPasskey, DateTimeOffset.UtcNow, CancellationToken.None))
            .Should().NotBeNull();
    }

    /// <summary>
    /// An account made through a provider has no password to prove and is not asked for one; it is
    /// asked to re-authenticate at the provider instead, and a registration without that proof is
    /// refused.
    /// </summary>
    [Fact]
    public async Task AnAccountWithoutAPasswordNeedsTheProvidersConfirmation()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddProviderUserAsync("provider@example.com", "subject-1");
        (PasskeysModel model, _) = rig.NewModel(user, password: null);

        // Act
        IActionResult page = await model.OnGetAsync();
        IActionResult begin = await model.OnPostBeginRegistrationAsync(CancellationToken.None);

        // Assert
        page.Should().BeOfType<PageResult>();
        model.HasPassword.Should().BeFalse();
        begin.Should().BeOfType<JsonResult>().Which.StatusCode.Should().Be(401, "nothing has confirmed the person at the provider");
    }

    /// <summary>The provider's confirmation is a five-minute proof the registration spends, once.</summary>
    [Fact]
    public async Task AProvidersConfirmationUnlocksOneRegistration()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddProviderUserAsync("provider@example.com", "subject-1");

        (_, DefaultHttpContext proofRequest) = rig.NewModel(user, password: null);
        rig.Ceremonies.Begin(proofRequest, PasskeyCeremonies.ProviderProof, "subject-1").Should().BeTrue();
        string proof = Rig.CookieOf(proofRequest);

        (PasskeysModel first, _) = rig.NewModel(user, cookie: proof, password: null);
        (PasskeysModel second, _) = rig.NewModel(user, cookie: proof, password: null);

        // Act
        IActionResult begin = await first.OnPostBeginRegistrationAsync(CancellationToken.None);
        IActionResult again = await second.OnPostBeginRegistrationAsync(CancellationToken.None);

        // Assert
        begin.Should().BeOfType<ContentResult>();
        again.Should().BeOfType<JsonResult>().Which.StatusCode.Should().Be(401, "the proof was spent by the first registration");
    }

    /// <summary>
    /// The re-authentication challenge asks the provider for a fresh sign-in, in both of the words
    /// providers understand, and goes only to a provider the account holds a login for.
    /// </summary>
    [Fact]
    public async Task ReauthenticatingChallengesTheProviderForAFreshSignIn()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddProviderUserAsync("provider@example.com", "subject-1");
        (PasskeysModel model, _) = rig.NewModel(user, password: null);

        // Act
        IActionResult result = await model.OnPostReauthenticateAsync(Schemes.ExternalOidc);
        IActionResult other = await model.OnPostReauthenticateAsync("some-other-provider");

        // Assert
        ChallengeResult challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.AuthenticationSchemes.Should().Equal(Schemes.ExternalOidc);
        OpenIdConnectChallengeProperties properties = challenge.Properties.Should().BeOfType<OpenIdConnectChallengeProperties>().Subject;
        properties.MaxAge.Should().Be(TimeSpan.Zero);
        properties.Prompt.Should().Be("login");
        properties.Items.Should().ContainKey("XsrfId").WhoseValue.Should().Be(user.Id.ToString(CultureInfo.InvariantCulture));
        other.Should().BeOfType<NotFoundResult>("the account holds no login with that provider");
    }

    /// <summary>
    /// A password account is not sent to a provider even if it also holds a login there: its password
    /// is the proof.
    /// </summary>
    [Fact]
    public async Task APasswordAccountIsNotSentToAProvider()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        (await rig.Users.AddLoginAsync(user, new UserLoginInfo(Schemes.ExternalOidc, "subject-9", "Dex"))).Succeeded.Should().BeTrue();
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult result = await model.OnPostReauthenticateAsync(Schemes.ExternalOidc);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // ---------- what the provider's answer must say ----------
    [Fact]
    public void AProvidersAnswerCountsOnlyForTheSubjectTheAccountSignsInWith()
    {
        DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        UserLoginInfo[] logins = [new(Schemes.ExternalOidc, "subject-1", "Dex")];

        PasskeysModel.ProviderProofRefusal(Answer("subject-1", authTime: null), logins, now)
                     .Should().BeNull("the subject matches and the provider reported no sign-in time, so it is taken at its word");
        PasskeysModel.ProviderProofRefusal(Answer("subject-2", authTime: null), logins, now)
                     .Should().Be("mismatch", "another account at the same provider is not this account re-authenticating");
        PasskeysModel.ProviderProofRefusal(Answer("subject-1", authTime: now.AddSeconds(-30)), logins, now)
                     .Should().BeNull("a sign-in half a minute ago is what max_age=0 asked for");
        PasskeysModel.ProviderProofRefusal(Answer("subject-1", authTime: now.AddMinutes(-10)), logins, now)
                     .Should().Be("stale", "the provider reused a session it already had instead of asking again");
    }

    private static ExternalLoginInfo Answer(string subject, DateTimeOffset? authTime)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, subject)];

        if (authTime is { } time)
        {
            claims.Add(new Claim("auth_time", time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        return new ExternalLoginInfo(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), Schemes.ExternalOidc, subject, "Dex");
    }

    // ---------- renaming and removing ----------
    [Fact]
    public async Task RenamingChangesTheName()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = await rig.SeedPasskeyAsync(user, "old");
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult result = await model.OnPostRenameAsync(PasskeysModel.IdOf(passkey), "  new  ");

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await rig.Users.GetPasskeyAsync(user, passkey.CredentialId))!.Name.Should().Be("new");
    }

    /// <summary>
    /// A name with a control character or an invisible mark is refused on both verbs, the rule the
    /// file store's directory name applies: the name is rendered in a table, and logged.
    /// </summary>
    [Theory]
    [InlineData("phone\u0000")]
    [InlineData("pho\nne")]
    [InlineData("phone\u202E")]
    [InlineData("\u200Bphone")]
    public async Task ANameWithAnInvisibleCharacterIsRefused(string name)
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = await rig.SeedPasskeyAsync(user, "old");
        using FakeAuthenticator authenticator = new();

        (PasskeysModel begin, DefaultHttpContext beginRequest) = rig.NewModel(user);
        ContentResult options = (await begin.OnPostBeginRegistrationAsync(CancellationToken.None)).Should().BeOfType<ContentResult>().Subject;
        (PasskeysModel register, _) = rig.NewModel(user, cookie: Rig.CookieOf(beginRequest));
        register.Input.Name = name;
        (PasskeysModel rename, _) = rig.NewModel(user);

        // Act
        IActionResult registered = await register.OnPostRegisterAsync(authenticator.Attest(options.Content!));
        IActionResult renamed = await rename.OnPostRenameAsync(PasskeysModel.IdOf(passkey), name);

        // Assert
        registered.Should().BeOfType<PageResult>();
        renamed.Should().BeOfType<PageResult>();
        (await rig.Users.GetPasskeysAsync(user)).Should().ContainSingle().Which.Name.Should().Be("old");
    }

    [Fact]
    public async Task RenamingToNothingIsRefused()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = await rig.SeedPasskeyAsync(user, "old");
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult result = await model.OnPostRenameAsync(PasskeysModel.IdOf(passkey), "   ");

        // Assert
        result.Should().BeOfType<PageResult>();
        (await rig.Users.GetPasskeyAsync(user, passkey.CredentialId))!.Name.Should().Be("old");
    }

    [Fact]
    public async Task RemovingDeletesIt()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        UserPasskeyInfo passkey = await rig.SeedPasskeyAsync(user, "laptop");
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult result = await model.OnPostRemoveAsync(PasskeysModel.IdOf(passkey));

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await rig.Users.GetPasskeysAsync(user)).Should().BeEmpty();
        model.StatusMessage.Should().Be("Passkey removed.");
    }

    /// <summary>
    /// Somebody else's credential id is "already gone" from this account's point of view, and stays
    /// where it is - the same answer as for a stale id, so the form reports nothing about other
    /// people's credentials.
    /// </summary>
    [Fact]
    public async Task RemovingAnotherAccountsPasskeyReportsGoneAndLeavesIt()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser owner = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        UserPasskeyInfo passkey = await rig.SeedPasskeyAsync(owner, "laptop");
        (PasskeysModel model, _) = rig.NewModel(other);

        // Act
        IActionResult result = await model.OnPostRemoveAsync(PasskeysModel.IdOf(passkey));

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("That passkey was already gone.");
        (await rig.Users.GetPasskeysAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task AMalformedIdIs404()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        (PasskeysModel model, _) = rig.NewModel(user);

        // Act
        IActionResult removed = await model.OnPostRemoveAsync("not base64url!");
        IActionResult renamed = await model.OnPostRenameAsync(string.Empty, "x");

        // Assert
        removed.Should().BeOfType<NotFoundResult>();
        renamed.Should().BeOfType<NotFoundResult>();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly HomespoolDbContext _context;
        private readonly IServiceProvider _provider;

        private Rig(HomespoolDbContext context, IServiceProvider provider)
        {
            _context = context;
            _provider = provider;
        }

        public UserManager<HSUser> Users => _provider.GetRequiredService<UserManager<HSUser>>();

        public IPasskeyHandler<HSUser> Engine => _provider.GetRequiredService<IPasskeyHandler<HSUser>>();

        public PasskeyCeremonies Ceremonies => _provider.GetRequiredService<PasskeyCeremonies>();

        public AttemptLimiter Limiter => new(_context, TestOptions.Snapshot(new AttemptLimitOptions()), NullLogger<AttemptLimiter>.Instance);

        public static async Task<Rig> CreateAsync(PasskeysPageTests owner)
        {
            DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                           .UseSqlite($"Data Source={owner._databasePath}")
                                                           .Options;

            HomespoolDbContext context = new(options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            (_, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(
                context,
                services => services.Configure<Middleware.SecurityOptions>(security => security.PasskeyServerDomain = RelyingPartyId));

            return new Rig(context, provider);
        }

        /// <summary>
        /// The page model over a request from <paramref name="user"/>, signed in, on <paramref name="host"/>,
        /// with <paramref name="password"/> typed into the add form - the right one unless a test says otherwise.
        /// </summary>
        public (PasskeysModel model, DefaultHttpContext request) NewModel(HSUser user, string? cookie = null, string host = RelyingPartyId, string? password = Password)
        {
            DefaultHttpContext request = new() { RequestServices = _provider };
            request.Request.Scheme = "https";
            request.Request.Host = new HostString(host);
            request.Request.Path = PagePath;
            request.Request.Headers.Origin = "https://" + host;
            request.Response.Body = new MemoryStream();

            if (cookie is not null)
            {
                request.Request.Headers.Cookie = cookie;
            }

            IdentityTestHarness.SignInAsPrincipal(request, user);

            PasskeysModel model = new(Users,
                                      _provider.GetRequiredService<SignInManager<HSUser>>(),
                                      Engine,
                                      Ceremonies,
                                      _provider.GetRequiredService<IOptionsMonitor<PasskeyAuthenticationOptions>>(),
                                      Limiter,
                                      TimeProvider.System,
                                      TestLocaliser.Shared(),
                                      NullLogger<PasskeysModel>.Instance)
            {
                PageContext = IdentityTestHarness.NewPageContext(request),
                Url = IdentityTestHarness.NewUrlHelper(request),
                Input = new PasskeysModel.InputModel { Password = password },
            };

            return (model, request);
        }

        /// <summary>An account made through a provider: no password, one login.</summary>
        public async Task<HSUser> AddProviderUserAsync(string email, string subject)
        {
            HSUser user = new(IdentityTestHarness.UsernameFor(email))
            {
                Email = email,
                EmailConfirmed = true,
            };

            (await Users.CreateAsync(user)).Succeeded.Should().BeTrue();
            (await Users.AddLoginAsync(user, new UserLoginInfo(Schemes.ExternalOidc, subject, "Dex"))).Succeeded.Should().BeTrue();

            return user;
        }

        public async Task<HSUser> AddUserAsync(string email)
        {
            HSUser user = new(IdentityTestHarness.UsernameFor(email))
            {
                Email = email,
                EmailConfirmed = true,
            };

            IdentityResult created = await Users.CreateAsync(user, Password);
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

            return user;
        }

        /// <summary>A stored credential record, bypassing the ceremony, for the verbs that act on one.</summary>
        public async Task<UserPasskeyInfo> SeedPasskeyAsync(HSUser user, string name)
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

            (await Users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();

            return passkey;
        }

        public static string CookieOf(DefaultHttpContext request)
        {
            StringValues setCookie = request.Response.Headers.SetCookie;
            string header = setCookie.ToString();
            header.Should().NotBeNullOrEmpty("the request should have started a ceremony");

            return header[..header.IndexOf(';', StringComparison.Ordinal)];
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
        }
    }
}
