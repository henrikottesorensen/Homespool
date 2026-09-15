using System;
using System.Collections.Generic;
using System.Globalization;
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
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Test;
using Homespool.Model.Entities;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// An account without a password proves itself at its provider, against a real dex: the proof unlocks
/// what it gates, and a provider's answer is only ever read by the flow that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What dex's mock connector does and does not do</b>, measured before this was written: it
/// accepts <c>max_age=0</c> and <c>prompt=login</c> without complaint, signs nobody in because it has
/// no login screen, reports no <c>auth_time</c>, and always vouches for the same subject. So these
/// prove the round trip, the subject check, and that the provider handler stamps this server's
/// <c>auth_time</c> on the answer - without it the proof is refused; the "asked again" half is the
/// provider's promise, and the page takes a provider that reports no sign-in time at its word.
/// </para>
/// <para>
/// <b>The fixed subject is what makes the second test possible.</b> An account linked to some other
/// subject, sent round to dex, comes back with an answer naming a stranger - which is exactly "signs in
/// at the provider as somebody else", the step the walk-around needs.
/// </para>
/// </remarks>
public sealed class ProviderReauthenticationDexTests
{
    /// <summary>The fixed subject dex's mock connector vouches for, read off a real id token.</summary>
    private const string MockSubject = DexFixture.MockSubject;

    private const string PasskeysPath = "/Account/Manage/Passkeys";

    private static readonly Uri AppBaseAddress = new("https://localhost/");

    [RequiresDexFact]
    public async Task AProviderAccountProvesAtTheProviderAndThenAddsAPasskey()
    {
        using Fixture fixture = new();
        HSUser user = await fixture.CreateProviderUserAsync(MockSubject);
        using HttpClient client = await fixture.SignInAsAsync(user);
        using FakeAuthenticator authenticator = new() { Origin = "https://localhost" };

        // Unproved, the page offers the way to prove rather than the add form.
        string before = await client.GetStringAsync(PasskeysPath, TestContext.Current.CancellationToken);
        before.Should().Contain("/Account/Reauthenticate").And.NotContain("passkey-register-form");

        // The round trip, started from the proof page's provider button and returned to its handler.
        using HttpResponseMessage signin = await fixture.DriveProviderRoundTripAsync(
            client, "/Account/Reauthenticate", PasskeysPath, "Provider", TestContext.Current.CancellationToken);

        using HttpResponseMessage returned = await client.GetAsync(signin.Headers.Location, TestContext.Current.CancellationToken);
        returned.StatusCode.Should().Be(HttpStatusCode.Redirect, "a confirmed round trip is a proof, and the proof returns to where it was going");
        returned.Headers.Location!.OriginalString.Should().Contain(PasskeysPath);

        // Proved, the add form is offered and the ceremony starts.
        string page = await client.GetStringAsync(PasskeysPath, TestContext.Current.CancellationToken);
        page.Should().Contain("passkey-register-form");
        string token = AntiforgeryTestHelper.ExtractToken(page);

        using FormUrlEncodedContent beginBody = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        using HttpResponseMessage begin = await client.PostAsync($"{PasskeysPath}?handler={PasskeysModel.BeginRegistrationHandler}", beginBody, TestContext.Current.CancellationToken);
        begin.StatusCode.Should().Be(HttpStatusCode.OK, "the provider's confirmation unlocks the ceremony");
        string creationOptions = await begin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent registerBody = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Name"] = "Phone",
            [PasskeyCredential.FormField] = authenticator.Attest(creationOptions),
        });

        using HttpResponseMessage registered = await client.PostAsync($"{PasskeysPath}?handler=Register", registerBody, TestContext.Current.CancellationToken);
        registered.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await fixture.PasskeyCountAsync(user)).Should().Be(1);
    }

    /// <summary>
    /// The walk-around the flow item closes, against a real provider: a session on a provider-only
    /// account starts a re-authentication, which needs no proof, and dex answers for a different
    /// subject. Opening the link callback with that answer, instead of returning to the proof page,
    /// links nothing.
    /// </summary>
    [RequiresDexFact]
    public async Task AReauthenticationsAnswerCannotBeLinkedAtTheLinkCallback()
    {
        using Fixture fixture = new();
        HSUser victim = await fixture.CreateProviderUserAsync("some-other-subject");
        using HttpClient client = await fixture.SignInAsAsync(victim);

        using HttpResponseMessage signin = await fixture.DriveProviderRoundTripAsync(
            client, "/Account/Reauthenticate", "/Account/Manage", "Provider", TestContext.Current.CancellationToken);

        signin.Headers.Location!.OriginalString.Should().Contain("ProviderReturned", "the provider sends the answer back to the proof page");

        // Act - the answer is taken to the link callback instead.
        using HttpResponseMessage linked = await client.GetAsync("/Account/Manage/ExternalLogins?handler=LinkLoginCallback", TestContext.Current.CancellationToken);

        // Assert
        linked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await fixture.LoginsAsync(victim)).Should().ContainSingle()
            .Which.ProviderKey.Should().Be("some-other-subject", "the stranger dex vouched for was not linked");
    }

    /// <summary>A host configured against dex with passkeys bound to localhost, and a dex client to walk its hops.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ScratchDirectory _scratch = ScratchDirectory.Create("provider-proof");
        private readonly HomespoolFactory _factory;
        private readonly HttpClientHandler _dexHandler;
        private readonly HttpClient _dex;

        public Fixture()
        {
            _factory = new HomespoolFactory(_scratch);

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

        /// <summary>An account with no password and one login: <paramref name="subject"/> at the provider.</summary>
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

        public async Task<IList<UserLoginInfo>> LoginsAsync(HSUser user)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser tracked = (await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;

            return await users.GetLoginsAsync(tracked);
        }

        /// <summary>
        /// Posts <paramref name="handler"/> on <paramref name="pagePath"/> with the page's antiforgery
        /// token and <paramref name="returnUrl"/>, walks dex's hops by hand as the sibling suite does,
        /// hands the code to the provider handler's callback, and returns that callback's answer - the
        /// redirect to the page's own return handler, not yet followed, with the external cookie set.
        /// </summary>
        public async Task<HttpResponseMessage> DriveProviderRoundTripAsync(HttpClient app,
                                                                           string pagePath,
                                                                           string returnUrl,
                                                                           string handler,
                                                                           CancellationToken cancellationToken)
        {
            string query = $"?returnUrl={Uri.EscapeDataString(returnUrl)}";
            string page = await app.GetStringAsync(pagePath + query, cancellationToken);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
                ["provider"] = Schemes.ExternalOidc,
            });

            using HttpResponseMessage challenge = await app.PostAsync($"{pagePath}{query}&handler={handler}", body, cancellationToken);

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

            HttpResponseMessage signin = await app.GetAsync(next.PathAndQuery, cancellationToken);

            signin.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                          "the handler consumes the code and hands off to the page's callback, but answered {0}: {1}",
                                          signin.StatusCode,
                                          signin.StatusCode == HttpStatusCode.Redirect
                                              ? string.Empty
                                              : await signin.Content.ReadAsStringAsync(cancellationToken));

            return signin;
        }

        public void Dispose()
        {
            _dex.Dispose();
            _dexHandler.Dispose();
            _factory.Dispose();
            _scratch.Dispose();
        }
    }
}
