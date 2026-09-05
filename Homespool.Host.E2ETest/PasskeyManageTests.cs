using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Test;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The passkey screens over HTTP: adding a passkey on the Manage page and then signing in with it,
/// and an administrator seeing and revoking it.
/// </summary>
/// <remarks>
/// The client half of each ceremony is <see cref="FakeAuthenticator"/>; everything server-side is the
/// real pipeline. The relying-party id is <c>localhost</c>, which the test client arrives as.
/// </remarks>
public sealed class PasskeyManageTests : IAsyncLifetime, IDisposable
{
    private const string RelyingPartyId = "localhost";
    private const string Origin = "http://localhost";
    private const string ManagePath = "/Account/Manage/Passkeys";
    private const string AdminPath = "/Admin/Passkeys";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-passkey-manage-{Guid.NewGuid():N}.db");
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
    public async Task TheManagePageOffersToAddAPasskey()
    {
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");

        using (client)
        {
            HttpResponseMessage page = await client.GetAsync(ManagePath, TestContext.Current.CancellationToken);
            string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            page.StatusCode.Should().Be(HttpStatusCode.OK);
            html.Should().Contain("id=\"passkey-register-form\"");
            html.Should().Contain("You have no passkeys yet.");
        }
    }

    [Fact]
    public async Task TheManagePageSaysWhichAddressToComeBackByOnAnUncoveredHost()
    {
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");

        using (client)
        {
            client.DefaultRequestHeaders.Host = "homespool.lan";

            HttpResponseMessage page = await client.GetAsync(ManagePath, TestContext.Current.CancellationToken);
            string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            page.StatusCode.Should().Be(HttpStatusCode.OK);
            html.Should().NotContain("id=\"passkey-register-form\"");
            html.Should().Contain("Passkeys are bound to localhost");
        }
    }

    /// <summary>The whole arc: add a passkey on the Manage page, sign out, sign in with it.</summary>
    [Fact]
    public async Task APasskeyAddedOnTheManagePageSignsIn()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        using FakeAuthenticator authenticator = new() { Origin = Origin };

        using (client)
        {
            client.DefaultRequestHeaders.Add("Origin", Origin);

            HttpResponseMessage page = await client.GetAsync(ManagePath, TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            using FormUrlEncodedContent beginBody = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            HttpResponseMessage begin = await client.PostAsync($"{ManagePath}?handler=BeginRegistration", beginBody, TestContext.Current.CancellationToken);
            begin.StatusCode.Should().Be(HttpStatusCode.OK);
            string creationOptions = await begin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using JsonDocument options = JsonDocument.Parse(creationOptions);
            options.RootElement.GetProperty("rp").GetProperty("id").GetString().Should().Be(RelyingPartyId);
            options.RootElement.GetProperty("user").GetProperty("name").GetString().Should().Be(user.UserName);

            using FormUrlEncodedContent registerBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Name"] = "MacBook",
                [PasskeyAuthenticationOptions.CredentialFormField] = authenticator.Attest(creationOptions),
            });

            // Act
            HttpResponseMessage registered = await client.PostAsync($"{ManagePath}?handler=Register", registerBody, TestContext.Current.CancellationToken);
            HttpResponseMessage listed = await client.GetAsync(ManagePath, TestContext.Current.CancellationToken);
            string html = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            registered.StatusCode.Should().Be(HttpStatusCode.Redirect);
            html.Should().Contain("MacBook");
            html.Should().Contain("Passkey added.");
        }

        // And now the arc's other end, on a fresh anonymous client.
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add("Origin", Origin);

        HttpResponseMessage login = await anonymous.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string loginToken = AntiforgeryTestHelper.ExtractToken(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent challengeBody = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = loginToken });
        HttpResponseMessage challenge = await anonymous.PostAsync("/Account/Login?handler=PasskeyOptions", challengeBody, TestContext.Current.CancellationToken);
        string requestOptions = await challenge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent assertionBody = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            [PasskeyAuthenticationOptions.CredentialFormField] = authenticator.Assert(requestOptions, user.Id.ToString(CultureInfo.InvariantCulture)),
            ["rememberMe"] = "false",
        });

        HttpResponseMessage signedIn = await anonymous.PostAsync("/Account/Login?handler=Passkey", assertionBody, TestContext.Current.CancellationToken);

        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "the passkey just added is a complete sign-in");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, signedIn).Should().BeTrue();
    }

    [Fact]
    public async Task AnAdministratorSeesEveryPasskeyAndRevokesOne()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        ownerClient.Dispose();
        UserPasskeyInfo passkey = await SeedPasskeyAsync(owner, "phone");

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "admin@example.com", AdminBootstrap.AdminRole);

        using (admin)
        {
            HttpResponseMessage before = await admin.GetAsync(AdminPath, TestContext.Current.CancellationToken);
            string beforeHtml = await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(beforeHtml);

            using FormUrlEncodedContent revokeBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["userId"] = owner.Id.ToString(CultureInfo.InvariantCulture),
                ["id"] = Convert.ToBase64String(passkey.CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            });

            // Act
            HttpResponseMessage revoked = await admin.PostAsync($"{AdminPath}?handler=Revoke", revokeBody, TestContext.Current.CancellationToken);
            HttpResponseMessage after = await admin.GetAsync(AdminPath, TestContext.Current.CancellationToken);
            string afterHtml = await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            before.StatusCode.Should().Be(HttpStatusCode.OK);
            beforeHtml.Should().Contain(owner.UserName!).And.Contain("phone");
            revoked.StatusCode.Should().Be(HttpStatusCode.Redirect);
            afterHtml.Should().Contain("Passkey revoked.").And.Contain("No passkeys have been enrolled.");
        }
    }

    [Fact]
    public async Task ANonAdministratorCannotReachTheAdminScreen()
    {
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");

        using (client)
        {
            HttpResponseMessage page = await client.GetAsync(AdminPath, TestContext.Current.CancellationToken);

            page.StatusCode.Should().NotBe(HttpStatusCode.OK, "the screen lists other people's credentials");
        }
    }

    private async Task<UserPasskeyInfo> SeedPasskeyAsync(HSUser user, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser tracked = (await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;

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

        (await users.AddOrUpdatePasskeyAsync(tracked, passkey)).Succeeded.Should().BeTrue();

        return passkey;
    }
}
