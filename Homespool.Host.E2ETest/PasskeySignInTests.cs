using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Test;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Signing in with a passkey over HTTP, the way the script on the login page does it: a post for the
/// challenge, an assertion signed by <see cref="FakeAuthenticator"/>, a post with the answer, and
/// the application cookie at the end of it - or the login page with its message.
/// </summary>
/// <remarks>
/// <b>What a browser would do is the only thing missing.</b> The test host has no
/// <c>navigator.credentials</c>, so the ceremony's client half is the fake authenticator; everything
/// server-side is the real pipeline, antiforgery and cookies included. The relying-party id is
/// <c>localhost</c>, which is what the test client arrives as and its own relying-party id in a
/// browser too.
/// </remarks>
public sealed class PasskeySignInTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";
    private const string Email = "passkey@example.com";
    private const string RelyingPartyId = "localhost";
    private const string Origin = "http://localhost";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-passkey-e2e-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
        _factory.ConfigurationOverrides["Security:PasskeyServerDomain"] = RelyingPartyId;
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TheLoginPageOffersAPasskeyWhereTheRelyingPartyIdCoversTheHost()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("id=\"passkey-signin\"");
        html.Should().Contain("/js/passkey-signin.", "the script is served under its fingerprinted name");
    }

    [Fact]
    public async Task TheLoginPageWithholdsThePasskeyOnAHostTheRelyingPartyIdDoesNotCover()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Host = "homespool.lan";

        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().NotContain("id=\"passkey-signin\"", "a ceremony from this host would fail in the browser, so the button is not offered");
    }

    [Fact]
    public async Task AGoodAssertionSignsIn()
    {
        // Arrange
        using FakeAuthenticator authenticator = new() { Origin = Origin };
        HSUser user = await CreateUserWithPasskeyAsync(authenticator);
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            string requestOptions = await ChallengeAsync(client, antiforgeryToken);
            string credential = authenticator.Assert(requestOptions, user.Id.ToString());

            // Act
            HttpResponseMessage response = await PostAssertionAsync(client, antiforgeryToken, credential);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a verified passkey is a complete sign-in");
            response.Headers.Location!.OriginalString.Should().Be("/");
            IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, response).Should()
                                    .BeTrue("the sign-in must issue the Identity application cookie");
        }
    }

    [Fact]
    public async Task AnAssertionFromAnotherOriginIsRefusedWithThePasswordMessage()
    {
        // Arrange
        using FakeAuthenticator authenticator = new() { Origin = "https://evil.test" };
        HSUser user = await CreateUserWithPasskeyAsync(authenticator);
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            string requestOptions = await ChallengeAsync(client, antiforgeryToken);
            string credential = authenticator.Assert(requestOptions, user.Id.ToString());

            // Act
            HttpResponseMessage response = await PostAssertionAsync(client, antiforgeryToken, credential);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "a refused assertion re-renders the page");
            IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, response).Should().BeFalse();
            html.Should().Contain("Invalid login attempt", "a passkey refusal reads exactly like a wrong password");
        }
    }

    [Fact]
    public async Task AnAssertionWithoutAChallengeIsRefused()
    {
        // Arrange
        using FakeAuthenticator authenticator = new() { Origin = Origin };
        HSUser user = await CreateUserWithPasskeyAsync(authenticator);
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            // A challenge taken on one client, answered on another that never saw its cookie.
            (HttpClient other, string otherToken) = await OpenLoginPageAsync();
            using (other)
            {
                string requestOptions = await ChallengeAsync(other, otherToken);
                string credential = authenticator.Assert(requestOptions, user.Id.ToString());

                // Act
                HttpResponseMessage response = await PostAssertionAsync(client, antiforgeryToken, credential);

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, response).Should().BeFalse();
            }
        }
    }

    [Fact]
    public async Task AnUnconfirmedAccountCannotSignInWithAPasskey()
    {
        // Arrange
        using FakeAuthenticator authenticator = new() { Origin = Origin };
        HSUser user = await CreateUserWithPasskeyAsync(authenticator, confirmed: false);
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            string requestOptions = await ChallengeAsync(client, antiforgeryToken);
            string credential = authenticator.Assert(requestOptions, user.Id.ToString());

            // Act
            HttpResponseMessage response = await PostAssertionAsync(client, antiforgeryToken, credential);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the confirmed-account rule holds for a passkey as it does for a password");
            IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, response).Should().BeFalse();
        }
    }

    /// <summary>
    /// The page does not gate the assertion on the host itself; the scheme does, at both ends. Pinned
    /// through the page so the reliance is a tested one: a ceremony started on the covered host and
    /// answered on an uncovered one is refused with the password message and no cookie.
    /// </summary>
    [Fact]
    public async Task AnAssertionPostedFromAnUncoveredHostIsRefused()
    {
        // Arrange
        using FakeAuthenticator authenticator = new() { Origin = Origin };
        HSUser user = await CreateUserWithPasskeyAsync(authenticator);
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            string requestOptions = await ChallengeAsync(client, antiforgeryToken);
            string credential = authenticator.Assert(requestOptions, user.Id.ToString());

            // The same client, cookies and all, now arriving by a name the relying-party id does not cover.
            client.DefaultRequestHeaders.Host = "homespool.lan";

            // Act
            HttpResponseMessage response = await PostAssertionAsync(client, antiforgeryToken, credential);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "a refused assertion re-renders the page");
            IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, response).Should().BeFalse();
            html.Should().Contain("Invalid login attempt");
        }
    }

    /// <summary>The challenge half answers 404 on an uncovered host, which is what the script hides the button on.</summary>
    [Fact]
    public async Task AChallengeFromAnUncoveredHostAnswers404()
    {
        (HttpClient client, string antiforgeryToken) = await OpenLoginPageAsync();

        using (client)
        {
            client.DefaultRequestHeaders.Host = "homespool.lan";

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgeryToken,
            });

            HttpResponseMessage response = await client.PostAsync("/Account/Login?handler=PasskeyOptions", body, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies);
            (cookies ?? []).Should().NotContain(
                cookie => cookie.StartsWith(PasskeyAuthenticationOptions.DefaultCeremonyCookieName, StringComparison.Ordinal),
                "no ceremony starts on a host the browser would refuse");
        }
    }

    /// <summary>
    /// The challenge is a POST behind the antiforgery token, so another site cannot start ceremonies
    /// against this one.
    /// </summary>
    [Fact]
    public async Task AChallengeWithoutTheAntiforgeryTokenIsRefused()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", Origin);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>());
        HttpResponseMessage response = await client.PostAsync("/Account/Login?handler=PasskeyOptions", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Seeds a confirmed account holding one passkey, enrolled through the engine's own attestation
    /// ceremony against the fake authenticator - the shortcut past the Manage page this suite takes
    /// because it verifies sign-in, not enrolment.
    /// </summary>
    private async Task<HSUser> CreateUserWithPasskeyAsync(FakeAuthenticator authenticator, bool confirmed = true)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        IPasskeyHandler<HSUser> engine = scope.ServiceProvider.GetRequiredService<IPasskeyHandler<HSUser>>();

        HSUser user = new(EnrolmentFlowHelper.UsernameFor(Email))
        {
            Email = Email,
            EmailConfirmed = confirmed,
        };

        IdentityResult created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");

        DefaultHttpContext request = new() { RequestServices = scope.ServiceProvider };
        request.Request.Scheme = "http";
        request.Request.Host = new HostString(RelyingPartyId);
        request.Request.Headers.Origin = Origin;

        // The enrolment ceremony is honest whatever the assertion later claims.
        string claimedOrigin = authenticator.Origin;
        authenticator.Origin = Origin;

        PasskeyCreationOptionsResult creation = await engine.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.UserName!, DisplayName = user.UserName! },
            request);

        PasskeyAttestationResult attested = await engine.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = request,
            CredentialJson = authenticator.Attest(creation.CreationOptionsJson),
            AttestationState = creation.AttestationState,
        });

        authenticator.Origin = claimedOrigin;

        attested.Succeeded.Should().BeTrue(attested.Failure?.Message);
        (await users.AddOrUpdatePasskeyAsync(user, attested.Passkey!)).Succeeded.Should().BeTrue();

        return user;
    }

    private async Task<(HttpClient client, string antiforgeryToken)> OpenLoginPageAsync()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", Origin);

        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return (client, token);
    }

    /// <summary>The first post the script makes: the request options, with the ceremony cookie riding back on the client.</summary>
    private static async Task<string> ChallengeAsync(HttpClient client, string antiforgeryToken)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
        });

        HttpResponseMessage response = await client.PostAsync("/Account/Login?handler=PasskeyOptions", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the relying-party id covers this host, so a challenge is issued");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeTrue("the challenge starts a ceremony");

        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument options = JsonDocument.Parse(json);
        options.RootElement.GetProperty("rpId").GetString().Should().Be(RelyingPartyId);

        return json;
    }

    /// <summary>The second post: the assertion in the form field the scheme reads.</summary>
    private static async Task<HttpResponseMessage> PostAssertionAsync(HttpClient client, string antiforgeryToken, string credential)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            [PasskeyAuthenticationOptions.CredentialFormField] = credential,
            ["rememberMe"] = "false",
        });

        return await client.PostAsync("/Account/Login?handler=Passkey", body, TestContext.Current.CancellationToken);
    }
}
