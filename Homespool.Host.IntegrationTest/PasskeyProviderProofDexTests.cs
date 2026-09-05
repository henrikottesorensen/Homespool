using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.E2ETest;
using Homespool.Host.Test;
using Homespool.Model.Entities;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// An account without a password adds a passkey by re-authenticating at its provider first: the
/// whole round trip against a real dex, then the registration the provider's confirmation unlocks.
/// </summary>
/// <remarks>
/// <b>What dex's mock connector does and does not do</b>, measured before this was written: it
/// accepts <c>max_age=0</c> and <c>prompt=login</c> without complaint, signs nobody in because it has
/// no login screen, and reports no <c>auth_time</c>. So this test proves the round trip, the
/// subject check and the proof's single use; the "asked again" half is the provider's promise, and
/// the page takes a provider that reports no sign-in time at its word.
/// </remarks>
public sealed class PasskeyProviderProofDexTests
{
    /// <summary>The fixed subject dex's mock connector vouches for, read off a real id token.</summary>
    private const string MockSubject = "Cg0wLTM4NS0yODA4OS0wEgRtb2Nr";

    private static readonly Uri AppBaseAddress = new("https://localhost/");

    [RequiresDexFact]
    public async Task AProviderAccountConfirmsAtTheProviderAndThenAddsAPasskey()
    {
        using Fixture fixture = new();
        HSUser user = await fixture.CreateProviderUserAsync(MockSubject);
        using HttpClient client = await fixture.SignInAsAsync(user);
        using FakeAuthenticator authenticator = new() { Origin = "https://localhost" };

        // The page offers the provider's confirmation and not a password.
        string page = await client.GetStringAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);
        page.Should().Contain("handler=Reauthenticate").And.NotContain("Input.Password");

        // Without the confirmation, no ceremony.
        string token = AntiforgeryTestHelper.ExtractToken(page);
        using HttpResponseMessage refused = await BeginAsync(client, token);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "nothing has confirmed the person at the provider");

        // The round trip: the page challenges dex, dex answers, the handler consumes the code, the
        // page's callback checks the subject and starts the proof.
        using HttpResponseMessage callback = await fixture.DriveReauthenticationAsync(client, token, TestContext.Current.CancellationToken);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect, "a confirmed round trip lands back on the page");
        callback.Headers.Location!.OriginalString.Should().Contain("/Account/Manage/Passkeys");

        string confirmed = await client.GetStringAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);
        confirmed.Should().Contain("confirmed you", "the status line says the provider vouched for the person");

        // Now the ceremony, and the registration it leads to.
        using HttpResponseMessage begin = await BeginAsync(client, token);
        begin.StatusCode.Should().Be(HttpStatusCode.OK, "the provider's confirmation unlocks the ceremony");
        string creationOptions = await begin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent registerBody = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Name"] = "Phone",
            [PasskeyAuthenticationOptions.CredentialFormField] = authenticator.Attest(creationOptions),
        });

        using HttpResponseMessage registered = await client.PostAsync("/Account/Manage/Passkeys?handler=Register", registerBody, TestContext.Current.CancellationToken);
        registered.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await fixture.PasskeyCountAsync(user)).Should().Be(1);

        // And the confirmation was spent: a second registration is refused until the provider is
        // asked again.
        using HttpResponseMessage spent = await BeginAsync(client, token);
        spent.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> BeginAsync(HttpClient client, string token)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

        return await client.PostAsync("/Account/Manage/Passkeys?handler=BeginRegistration", body, TestContext.Current.CancellationToken);
    }

    /// <summary>A host configured against dex with passkeys bound to localhost, and a dex client to walk its hops.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pkproof-{Guid.NewGuid():N}.db");
        private readonly HomespoolFactory _factory;
        private readonly HttpClientHandler _dexHandler;
        private readonly HttpClient _dex;

        public Fixture()
        {
            _factory = new HomespoolFactory($"Data Source={_databasePath}");

            _factory.ConfigurationOverrides["Oidc:Authority"] = DexFixture.Issuer;
            _factory.ConfigurationOverrides["Oidc:ClientId"] = DexFixture.ClientId;
            _factory.ConfigurationOverrides["Oidc:ClientSecret"] = DexFixture.ClientSecret;
            _factory.ConfigurationOverrides["Oidc:RequireHttpsMetadata"] = "false";
            _factory.ConfigurationOverrides["Security:PasskeyServerDomain"] = "localhost";

            _dexHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
            };

            _dex = new HttpClient(_dexHandler);

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        /// <summary>An account with no password and one login: the provider's subject.</summary>
        public async Task<HSUser> CreateProviderUserAsync(string subject)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

            HSUser user = new("kilgore") { Email = DexFixture.MockEmail, EmailConfirmed = true };

            (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
            (await users.AddLoginAsync(user, new UserLoginInfo(Schemes.ExternalOidc, subject, "Dex"))).Succeeded.Should().BeTrue();

            return user;
        }

        /// <summary>
        /// A signed-in client at the https base address the external-login handler's cookies require,
        /// carrying the application cookie as <see cref="EnrolmentFlowHelper.SignInAsAsync"/> mints it.
        /// </summary>
        public async Task<HttpClient> SignInAsAsync(HSUser user)
        {
            using HttpClient minted = await EnrolmentFlowHelper.SignInAsAsync(_factory, user);

            HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = AppBaseAddress,
            });

            foreach (string cookie in minted.DefaultRequestHeaders.GetValues("Cookie"))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
            }

            client.DefaultRequestHeaders.Add("Origin", "https://localhost");

            return client;
        }

        public async Task<int> PasskeyCountAsync(HSUser user)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser tracked = (await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;

            return (await users.GetPasskeysAsync(tracked)).Count;
        }

        /// <summary>
        /// Posts the page's re-authentication, walks dex's hops by hand as the sibling suite does, hands
        /// the code to the handler's callback, and returns the page callback's answer.
        /// </summary>
        public async Task<HttpResponseMessage> DriveReauthenticationAsync(HttpClient app, string token, CancellationToken cancellationToken)
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["provider"] = Schemes.ExternalOidc,
            });

            using HttpResponseMessage challenge = await app.PostAsync("/Account/Manage/Passkeys?handler=Reauthenticate", body, cancellationToken);

            challenge.StatusCode.Should().Be(HttpStatusCode.Redirect, "a provider account is sent to its provider");
            challenge.Headers.Location!.Query.Should().Contain("max_age=0").And.Contain("prompt=login",
                "the provider is asked for a fresh sign-in in both of the words providers understand");

            // Dex's own hops, resolved against the leg they came from because some are relative.
            Uri next = challenge.Headers.Location!;

            while (true)
            {
                Uri current = next;

                using HttpResponseMessage hop = await _dex.GetAsync(current, cancellationToken);

                hop.Headers.Location.Should().NotBeNull("every leg of the provider's flow is a redirect");

                Uri location = hop.Headers.Location!;
                next = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (!string.Equals(next.Authority, DexFixture.Authority, StringComparison.Ordinal))
                {
                    break;
                }
            }

            using HttpResponseMessage signin = await app.GetAsync(next.PathAndQuery, cancellationToken);

            signin.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                          "the handler consumes the code and hands off to the page's callback, but answered {0}: {1}",
                                          signin.StatusCode,
                                          signin.StatusCode == HttpStatusCode.Redirect
                                              ? string.Empty
                                              : await signin.Content.ReadAsStringAsync(cancellationToken));

            return await app.GetAsync(signin.Headers.Location, cancellationToken);
        }

        public void Dispose()
        {
            _dex.Dispose();
            _dexHandler.Dispose();
            _factory.Dispose();

            foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
