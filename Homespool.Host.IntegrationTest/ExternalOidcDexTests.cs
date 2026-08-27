using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.E2ETest;
using Homespool.Model.Entities;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// The external OpenID Connect handler against a real provider — a dex container issuing real
/// authorisation codes and real id tokens, driven through the whole redirect dance.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this covers that nothing else can.</b> The callback is a request, and the external cookie
/// it reads was written by the handler earlier in the same pipeline; a service-level test has neither.
/// More to the point, the thing under test is a <i>protocol</i> conversation with software nobody here
/// wrote — discovery, PKCE, the code exchange, claim shapes — and a stub of that only ever proves the
/// stub agrees with the assumptions that produced it.
/// </para>
/// <para>
/// <b>The test plays the browser.</b> Nothing listens on the redirect URI: the application is a
/// <see cref="WebApplicationFactory{TEntryPoint}"/> host with no socket, so dex's final redirect goes
/// nowhere on its own. That is fine, because a browser is only a courier here — it carries a
/// <c>code</c> from one party to the other, and <c>Fixture.DriveProviderSignInAsync</c> carries it by
/// hand instead. Dex never contacts the application; only the back channel (discovery, token, JWKS)
/// goes the other way, and that reaches a real container on loopback.
/// </para>
/// <para>
/// <b>Two clients, deliberately.</b> The application's carries the antiforgery and correlation cookies
/// the handler sets; dex's carries dex's own session cookies. One shared client would let dex's cookies
/// into the application's jar and vice versa, which is not what a browser does across origins and
/// would hide a bug where the handler depended on it.
/// </para>
/// <para>
/// The address and the <c>email_verified</c> claim below are dex's <c>mockCallback</c> connector's,
/// read off a real id token rather than assumed — see <c>dex.yaml</c> for why that connector rather
/// than a password one.
/// </para>
/// </remarks>
public sealed class ExternalOidcDexTests
{
    /// <summary>
    /// The application's own address in these tests, and <b>https on purpose</b>.
    /// </summary>
    /// <remarks>
    /// The handler writes its correlation and nonce cookies <c>secure; samesite=none</c> - none because
    /// the callback arrives from a cross-site redirect, and secure because a browser refuses
    /// <c>SameSite=None</c> without it. Over plain http a cookie jar quite correctly declines to send
    /// them back, and the callback fails correlation before it has parsed anything. So this is not a
    /// test convenience: external login cannot work over http at all, which is no constraint given TLS
    /// is the default. TestServer does no real TLS - the scheme is all this
    /// changes, and the scheme is the whole point.
    /// </remarks>
    private static readonly Uri AppBaseAddress = new("https://localhost/");

    /// <summary>
    /// A provider sign-in for an address nobody invited creates nothing, however well it authenticated.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the gate. Before it, the callback fell through to a confirmation page
    /// that created an account for any subject the provider vouched for — which is open registration
    /// wearing a provider's clothes, on a deployment whose registration is invite-only.
    /// </remarks>
    [RequiresDexFact]
    public async Task AProviderSignInWithNoInviteCreatesNoAccount()
    {
        using Fixture fixture = new(allowInviteMatchByEmail: true);

        HttpResponseMessage callback = await fixture.DriveProviderSignInAsync(TestContext.Current.CancellationToken);

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                        "a sign-in that may not create an account is sent back to the login page");
        callback.Headers.Location!.OriginalString.Should().Contain("/Account/Login");

        (await fixture.FindUserAsync(DexFixture.MockEmail)).Should().BeNull("nothing authorised an account to exist");

        callback.Dispose();
    }

    /// <summary>
    /// With the option on and the provider asserting it verified the address, an outstanding invite for
    /// that address is claimable — and is spent exactly once.
    /// </summary>
    [RequiresDexFact]
    public async Task AnInviteIsClaimedByAVerifiedAddressWhenTheOptionIsOn()
    {
        using Fixture fixture = new(allowInviteMatchByEmail: true);

        Invitation invitation = await fixture.CreateInviteAsync(DexFixture.MockEmail, TestContext.Current.CancellationToken);

        using HttpResponseMessage callback =
            await fixture.DriveProviderSignInAsync(TestContext.Current.CancellationToken);

        callback.StatusCode.Should().Be(HttpStatusCode.OK,
                                        "the invite authorises an account, so the confirmation form is shown");

        await fixture.ConfirmAsync(callback, "kilgore", TestContext.Current.CancellationToken);

        HSUser? created = await fixture.FindUserAsync(DexFixture.MockEmail);

        created.Should().NotBeNull("the invite authorised exactly this account");
        created!.Email.Should().Be(DexFixture.MockEmail);

        (await fixture.ReloadInviteAsync(invitation.Id, TestContext.Current.CancellationToken))!
            .UsedAt.Should().NotBeNull("accepting through a provider spends the invite like any other accept");
    }

    /// <summary>
    /// The same invite, the same verified address, the option off — refused. This is the setting doing
    /// its job rather than a default nobody chose.
    /// </summary>
    [RequiresDexFact]
    public async Task AVerifiedAddressDoesNotClaimAnInviteWhenTheOptionIsOff()
    {
        using Fixture fixture = new(allowInviteMatchByEmail: false);

        _ = await fixture.CreateInviteAsync(DexFixture.MockEmail, TestContext.Current.CancellationToken);

        using HttpResponseMessage callback =
            await fixture.DriveProviderSignInAsync(TestContext.Current.CancellationToken);

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Contain("/Account/Login");

        (await fixture.FindUserAsync(DexFixture.MockEmail)).Should()
            .BeNull("matching an invite by address is exactly what the option withholds");
    }

    /// <summary>
    /// An invite token carried through the provider round trip is accepted <em>with the option off</em>
    /// — the stronger door, which does not consult the provider's claims at all.
    /// </summary>
    [RequiresDexFact]
    public async Task AnInviteTokenCarriedThroughTheProviderIsAcceptedWithTheOptionOff()
    {
        using Fixture fixture = new(allowInviteMatchByEmail: false);

        (Invitation invitation, string token) =
            await fixture.CreateInviteWithTokenAsync(DexFixture.MockEmail, TestContext.Current.CancellationToken);

        using HttpResponseMessage callback = await fixture.DriveProviderSignInAsync(
            TestContext.Current.CancellationToken,
            invitation.Id,
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));

        callback.StatusCode.Should().Be(HttpStatusCode.OK,
                                        "possession of the token authorises the account without the option");

        await fixture.ConfirmAsync(callback, "kilgore", TestContext.Current.CancellationToken);

        (await fixture.FindUserAsync(DexFixture.MockEmail)).Should().NotBeNull();
    }

    /// <summary>
    /// A host configured against the dex fixture, plus the two clients and the seeding the tests need.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-oidc-{Guid.NewGuid():N}.db");
        private readonly HomespoolFactory _factory;
        private readonly HttpClient _app;
        private readonly HttpClientHandler _dexHandler;
        private readonly HttpClient _dex;

        public Fixture(bool allowInviteMatchByEmail)
        {
            _factory = new HomespoolFactory($"Data Source={_databasePath}");

            _factory.ConfigurationOverrides["Oidc:Authority"] = DexFixture.Issuer;
            _factory.ConfigurationOverrides["Oidc:ClientId"] = DexFixture.ClientId;
            _factory.ConfigurationOverrides["Oidc:ClientSecret"] = DexFixture.ClientSecret;

            // Loopback dex speaks plain HTTP; see OidcOptions.RequireHttpsMetadata for why that is a
            // fixture decision and not a relaxation of the production rule.
            _factory.ConfigurationOverrides["Oidc:RequireHttpsMetadata"] = "false";

            _factory.ConfigurationOverrides["Oidc:AllowInviteMatchByEmail"] =
                allowInviteMatchByEmail.ToString(CultureInfo.InvariantCulture);

            // Redirects are followed by hand throughout: which redirect arrived, and to where, is the
            // assertion in three of these tests.
            _app = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = AppBaseAddress,
            });

            _dexHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
            };

            _dex = new HttpClient(_dexHandler);

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        /// <summary>Creates an outstanding invite bound to <paramref name="email"/>.</summary>
        public async Task<Invitation> CreateInviteAsync(string email, CancellationToken cancellationToken)
        {
            (Invitation invitation, _) = await CreateInviteWithTokenAsync(email, cancellationToken);

            return invitation;
        }

        /// <summary>
        /// As <see cref="CreateInviteAsync"/>, but also returns the plaintext token — which exists only
        /// in this return value and is never stored, so a test that wants the token door must ask here.
        /// </summary>
        public async Task<(Invitation invitation, string token)> CreateInviteWithTokenAsync(
            string email,
            CancellationToken cancellationToken)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            // InvitedBy carries no foreign key, so an id nobody owns is enough here; who issued it is
            // audit information and no part of what these tests exercise.
            return await scope.ServiceProvider.GetRequiredService<InvitationService>()
                              .CreateAsync(email, teamId: null, invitedBy: 1, expiresAt: null, cancellationToken);
        }

        public async Task<Invitation?> ReloadInviteAsync(int id, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<Data.HomespoolDbContext>()
                              .Invitations.FindAsync([id], cancellationToken);
        }

        public async Task<HSUser?> FindUserAsync(string email)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>().FindByEmailAsync(email);
        }

        /// <summary>
        /// Runs a whole external sign-in and returns what the callback answered: the confirmation page
        /// when an invite authorised one, or the redirect back to the login page when nothing did.
        /// </summary>
        /// <param name="cancellationToken">Cancels every leg.</param>
        /// <param name="inviteId">An invite to carry through the provider, or null for the plain flow.</param>
        /// <param name="code">The invite's Base64Url token, required when <paramref name="inviteId"/> is given.</param>
        public async Task<HttpResponseMessage> DriveProviderSignInAsync(CancellationToken cancellationToken,
                                                                        int? inviteId = null,
                                                                        string? code = null)
        {
            string loginPage = await _app.GetStringAsync("/Account/Login", cancellationToken);

            Dictionary<string, string> form = new()
            {
                ["provider"] = Schemes.ExternalOidc,
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(loginPage),
            };

            if (inviteId is int id)
            {
                form["inviteId"] = id.ToString(CultureInfo.InvariantCulture);
                form["code"] = code!;
            }

            using FormUrlEncodedContent body = new(form);

            using HttpResponseMessage challenge =
                await _app.PostAsync("/Account/ExternalLogin", body, cancellationToken);

            challenge.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                             "a registered provider is challenged rather than refused");

            // Dex's own hops: the authorise endpoint picks the single connector, the connector calls
            // back into dex, and dex finally redirects to the application. Followed by hand so the
            // loop can stop the moment the destination stops being dex.
            Uri next = challenge.Headers.Location!;

            while (true)
            {
                Uri current = next;

                using HttpResponseMessage hop = await _dex.GetAsync(current, cancellationToken);

                hop.Headers.Location.Should().NotBeNull("every leg of the provider's flow is a redirect");

                // Resolved against the leg it came from, because dex answers some of these with a
                // relative Location and RFC 9110 allows it. A browser resolves; so does curl, which is
                // why driving this by hand at the shell did not show it.
                Uri location = hop.Headers.Location!;

                next = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (!string.Equals(next.Authority, DexFixture.Authority, StringComparison.Ordinal))
                {
                    break;
                }
            }

            // The courier's delivery: the code goes to the handler's callback path on the application,
            // which validates state, exchanges it with dex over the back channel and writes the external
            // cookie before redirecting on to the page that decides whether an account may exist.
            using HttpResponseMessage signin = await _app.GetAsync(next.PathAndQuery, cancellationToken);

            // The body on anything else, because a bare status code here is unactionable: the handler
            // faults for a dozen protocol reasons - a stale nonce, an unreachable back channel, a
            // rejected token - and every one of them shows up as a 500 with the answer in the page.
            signin.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                          "the handler consumes the code and hands off to ExternalLogin, but answered "
                                          + "{0}: {1}",
                                          signin.StatusCode,
                                          signin.StatusCode == HttpStatusCode.Redirect
                                              ? string.Empty
                                              : await signin.Content.ReadAsStringAsync(cancellationToken));

            return await _app.GetAsync(signin.Headers.Location, cancellationToken);
        }

        /// <summary>Posts the confirmation form, choosing <paramref name="username"/> for the new account.</summary>
        public async Task ConfirmAsync(HttpResponseMessage confirmationPage, string username,
                                       CancellationToken cancellationToken)
        {
            string page = await confirmationPage.Content.ReadAsStringAsync(cancellationToken);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["Input.Username"] = username,
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
            });

            using HttpResponseMessage confirmed =
                await _app.PostAsync("/Account/ExternalLogin?handler=Confirmation", body, cancellationToken);

            confirmed.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                             "a created account either signs in or holds at RegisterConfirmation");
        }

        public void Dispose()
        {
            _app.Dispose();
            _dex.Dispose();
            _dexHandler.Dispose();
            _factory.Dispose();

            try
            {
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
