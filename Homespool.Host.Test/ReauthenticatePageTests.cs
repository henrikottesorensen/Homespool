using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Account;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The one door a page that wants more than a session sends people through: which credentials open
/// it, what a proof grants, and what a wrong or borrowed credential leaves ungranted.
/// </summary>
/// <remarks>
/// <b>A refused proof must leave the browser exactly as unproved as it arrived</b>, which is the test
/// to write first - a page that granted regardless would satisfy every assertion about the happy path.
/// The passkey half's own first test is the borrowed one: the scheme names whoever's key signed, and
/// the page is what insists that it was this account's.
/// </remarks>
public sealed class ReauthenticatePageTests : IDisposable
{
    private const string RelyingPartyId = "homespool.test";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-reauth-{Guid.NewGuid():N}.db");

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
    public async Task TheRightPasswordProvesAndGoesWhereTheyWereHeading()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        (ReauthenticateModel model, DefaultHttpContext request) = await rig.PageAsync(user, LocalSchemeRig.Password);
        model.ReturnUrl = "/Admin/Users/Detail/3";

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Admin/Users/Detail/3");
        rig.Proved(request, user).Should().BeTrue();
    }

    [Fact]
    public async Task AWrongPasswordProvesNothing()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        (ReauthenticateModel model, DefaultHttpContext request) = await rig.PageAsync(user, "not it"); // betterleaks:allow

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>();
        model.StatusMessage.Should().Be("That is not your password.");
        rig.Proved(request, user).Should().BeFalse("a refused proof leaves the browser as it arrived");
        model.UsesPassword.Should().BeTrue("the page is rendered again with the ways open to this account");
    }

    /// <summary>
    /// An account with no password is not compared against one it does not have: the branch is chosen
    /// by what the account holds, not by what was posted.
    /// </summary>
    [Fact]
    public async Task APasswordlessAccountIsNotProvedByAPassword()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddPasswordlessUserAsync("provider@example.com");
        (ReauthenticateModel model, DefaultHttpContext request) = await rig.PageAsync(user, LocalSchemeRig.Password);

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>();
        rig.Proved(request, user).Should().BeFalse();
        model.UsesPassword.Should().BeFalse();
    }

    /// <summary>
    /// The scheme names whoever's key signed, and the page is what insists it was this account's. The
    /// challenge here is deliberately <i>not</i> bound to the session's account - a bound one lists
    /// only that account's credentials and the engine refuses the other key before the page sees it -
    /// so that what this pins is the page's own comparison, which is the check that survives a
    /// ceremony started somewhere else.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsPasskeyProvesNothing()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        using FakeAuthenticator othersKey = new();
        HSUser other = await rig.EnrolAsync("other@example.com", othersKey);
        (string credential, string ceremony) = await rig.AssertAsync(user, othersKey, other, bound: false);
        (ReauthenticateModel model, DefaultHttpContext request) = await rig.PageAsync(user, password: null, ceremony);

        // Act
        IActionResult result = await model.OnPostPasskeyAsync(credential);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.StatusMessage.Should().NotBeNullOrEmpty();
        rig.Proved(request, user).Should().BeFalse("a passkey that names another account is not this account's proof");
    }

    [Fact]
    public async Task ThisAccountsPasskeyProves()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        using FakeAuthenticator key = new();
        HSUser user = await rig.EnrolAsync("owner@example.com", key);
        (string credential, string ceremony) = await rig.AssertAsync(user, key, user);
        (ReauthenticateModel model, DefaultHttpContext request) = await rig.PageAsync(user, password: null, ceremony);
        model.ReturnUrl = "/Account/Manage/ApiTokens";

        // Act
        IActionResult result = await model.OnPostPasskeyAsync(credential);

        // Assert
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Manage/ApiTokens", "any local address is followed now that the proof is not scoped to a section");
        rig.Proved(request, user).Should().BeTrue();
    }

    [Fact]
    public async Task ThePasskeyIsOfferedOnlyToAnAccountThatHasOne()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser without = await rig.Schemes.AddUserAsync("owner@example.com");
        using FakeAuthenticator key = new();
        HSUser with = await rig.EnrolAsync("keyed@example.com", key);
        (ReauthenticateModel plain, _) = await rig.PageAsync(without, password: null);
        (ReauthenticateModel keyed, _) = await rig.PageAsync(with, password: null);

        // Act
        await plain.OnGetAsync();
        await keyed.OnGetAsync();

        // Assert
        plain.PasskeysAvailable.Should().BeFalse();
        keyed.PasskeysAvailable.Should().BeTrue();
    }

    /// <summary>A person already proved is not asked again: a bookmark or a back button goes straight on.</summary>
    [Fact]
    public async Task AProvedPersonIsSentStraightOn()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        (ReauthenticateModel proving, DefaultHttpContext proved) = await rig.PageAsync(user, LocalSchemeRig.Password);
        await proving.OnPostAsync();

        (ReauthenticateModel model, _) = await rig.PageAsync(user, password: null, Rig.ProofCookieOf(proved)!);
        model.ReturnUrl = "/Admin/Settings";

        IActionResult result = await model.OnGetAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Admin/Settings");
    }

    /// <summary>A sign-in is not a proof: a fresh session with no mark is asked, however recent the login.</summary>
    [Fact]
    public async Task AFreshSessionIsAskedToProve()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        (ReauthenticateModel model, _) = await rig.PageAsync(user, password: null);

        IActionResult result = await model.OnGetAsync();

        result.Should().BeOfType<PageResult>();
        model.UsesPassword.Should().BeTrue();
    }

    /// <summary>
    /// A return address off this site is not followed - the page would be an open redirect - and the
    /// account pages are where a proof with nowhere to go ends up.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example.com/")]
    [InlineData(null)]
    public async Task AnAddressOffThisSiteGoesToTheAccountPages(string? returnUrl)
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.Schemes.AddUserAsync("owner@example.com");
        (ReauthenticateModel model, _) = await rig.PageAsync(user, LocalSchemeRig.Password);
        model.ReturnUrl = returnUrl;

        IActionResult result = await model.OnPostAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Contain("/Account/Manage/Index");
    }

    /// <summary>
    /// The real Identity stack with the passkey scheme configured for <see cref="RelyingPartyId"/>, and
    /// the page model built over a request that is signed in the way a browser's would be: through the
    /// application cookie.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const string ProofCookieName = "Homespool.RecentProof";

        private Rig()
        {
        }

        public LocalSchemeRig Schemes { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(string databasePath)
        {
            Rig rig = new();

            rig.Schemes = await LocalSchemeRig.CreateAsync(databasePath, services =>
                services.Configure<Middleware.SecurityOptions>(security => security.PasskeyServerDomain = RelyingPartyId));

            return rig;
        }

        /// <summary>An account created through a provider: confirmed, and with no password to prove.</summary>
        public async Task<HSUser> AddPasswordlessUserAsync(string email)
        {
            HSUser user = new(IdentityTestHarness.UsernameFor(email)) { Email = email, EmailConfirmed = true };
            (await Schemes.Users.CreateAsync(user)).Succeeded.Should().BeTrue();

            return user;
        }

        /// <summary>A fresh account holding one passkey, registered through the engine's own attestation ceremony.</summary>
        public async Task<HSUser> EnrolAsync(string email, FakeAuthenticator authenticator)
        {
            HSUser user = await Schemes.AddUserAsync(email);
            DefaultHttpContext request = Schemes.NewRequest();
            IPasskeyHandler<HSUser> engine = request.RequestServices.GetRequiredService<IPasskeyHandler<HSUser>>();

            PasskeyCreationOptionsResult creation = await engine.MakeCreationOptionsAsync(
                new PasskeyUserEntity { Id = user.Id.ToString(CultureInfo.InvariantCulture), Name = user.UserName!, DisplayName = user.UserName! },
                request);

            PasskeyAttestationResult attested = await engine.PerformAttestationAsync(new PasskeyAttestationContext
            {
                HttpContext = request,
                CredentialJson = authenticator.Attest(creation.CreationOptionsJson),
                AttestationState = creation.AttestationState,
            });

            attested.Succeeded.Should().BeTrue(attested.Failure?.Message);
            attested.Passkey!.Name = "fake authenticator";
            (await Schemes.Users.AddOrUpdatePasskeyAsync(user, attested.Passkey)).Succeeded.Should().BeTrue();

            return user;
        }

        /// <summary>
        /// The two halves of a passkey proof from the browser's side: a challenge asked for by
        /// <paramref name="session"/>'s page - bound to that account as the page issues it, or not,
        /// as the login page's is - answered by <paramref name="authenticator"/> as
        /// <paramref name="holder"/>. Returns the assertion and the ceremony cookie to post it with.
        /// </summary>
        public async Task<(string credential, string ceremony)> AssertAsync(HSUser session, FakeAuthenticator authenticator, HSUser holder, bool bound = true)
        {
            DefaultHttpContext challenge = await SignedInRequestAsync(session);

            AuthenticationProperties properties = new();

            if (bound)
            {
                properties.Items[PasskeyAuthenticationHandler.UserIdProperty] = session.Id.ToString(CultureInfo.InvariantCulture);
            }

            await challenge.ChallengeAsync(Authentication.Schemes.Passkey, properties);

            challenge.Response.Body.Position = 0;
            using StreamReader reader = new(challenge.Response.Body, Encoding.UTF8, leaveOpen: true);
            string options = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            string setCookie = challenge.Response.Headers.SetCookie
                                        .First(value => value is not null && !value.StartsWith(Schemes.CookieNameOf(IdentityConstants.ApplicationScheme), StringComparison.Ordinal))!;

            return (authenticator.Assert(options, holder.Id.ToString(CultureInfo.InvariantCulture)), setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        }

        /// <summary>
        /// The page over a request signed in as <paramref name="user"/>, with <paramref name="password"/>
        /// posted and any other <paramref name="cookies"/> riding along.
        /// </summary>
        public async Task<(ReauthenticateModel model, DefaultHttpContext request)> PageAsync(HSUser user, string? password, params string[] cookies)
        {
            DefaultHttpContext request = await SignedInRequestAsync(user, cookies);
            IServiceProvider services = request.RequestServices;

            ReauthenticateModel model = new(services.GetRequiredService<UserManager<HSUser>>(),
                                            services.GetRequiredService<RecentProof>(),
                                            services.GetRequiredService<LocalSignInRules>(),
                                            services.GetRequiredService<StepUpGate>(),
                                            new StepUpText(TestLocaliser.Shared()),
                                            services.GetRequiredService<ExternalSignIn>(),
                                            services.GetRequiredService<IOptionsMonitor<PasskeyAuthenticationOptions>>(),
                                            TestLocaliser.Shared(),
                                            NullLogger<ReauthenticateModel>.Instance)
            {
                PageContext = IdentityTestHarness.NewPageContext(request),
                Url = IdentityTestHarness.NewUrlHelper(request),
                Input = new ReauthenticateModel.InputModel { Password = password },
            };

            return (model, request);
        }

        /// <summary>Whether the response to <paramref name="request"/> proved <paramref name="user"/>, read as the next request would.</summary>
        public bool Proved(DefaultHttpContext request, HSUser user)
        {
            string? cookie = ProofCookieOf(request);

            if (cookie is null)
            {
                return false;
            }

            DefaultHttpContext next = Schemes.NewRequest(cookie);

            return next.RequestServices.GetRequiredService<RecentProof>().IsProved(next, user.Id);
        }

        /// <summary>The proof cookie <paramref name="request"/> set, as the browser would send it back, or null when it set none.</summary>
        public static string? ProofCookieOf(DefaultHttpContext request)
        {
            string? header = request.Response.Headers.SetCookie
                                    .FirstOrDefault(value => value is not null && value.StartsWith(ProofCookieName + "=", StringComparison.Ordinal) && !value.StartsWith(ProofCookieName + "=;", StringComparison.Ordinal));

            return header?[..header.IndexOf(';', StringComparison.Ordinal)];
        }

        /// <summary>
        /// A request from a browser signed in as <paramref name="user"/>: the application cookie a
        /// real sign-in wrote, authenticated, and its principal on the request as the pipeline would
        /// leave it.
        /// </summary>
        private async Task<DefaultHttpContext> SignedInRequestAsync(HSUser user, params string[] cookies)
        {
            DefaultHttpContext request = Schemes.NewRequest([await Schemes.SessionCookieAsync(user), .. cookies]);
            request.Request.Path = "/Account/Reauthenticate";
            request.Request.Headers.Origin = $"https://{RelyingPartyId}";
            request.Response.Body = new MemoryStream();
            request.User = (await request.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Principal!;

            return request;
        }

        public async ValueTask DisposeAsync()
        {
            await Schemes.DisposeAsync();
        }
    }
}
