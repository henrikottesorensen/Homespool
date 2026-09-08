using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;
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
public sealed class PasskeyManageTests : IAsyncLifetime
{
    private const string RelyingPartyId = "localhost";
    private const string Origin = "http://localhost";
    private const string ManagePath = "/Account/Manage/Passkeys";
    private const string AdminPath = "/Admin/Users";
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("passkey-manage");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _factory.ConfigurationOverrides["Security:PasskeyServerDomain"] = RelyingPartyId;
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
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

            using FormUrlEncodedContent beginBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Password"] = Password,
            });
            HttpResponseMessage begin = await client.PostAsync($"{ManagePath}?handler={PasskeysModel.BeginRegistrationHandler}", beginBody, TestContext.Current.CancellationToken);
            begin.StatusCode.Should().Be(HttpStatusCode.OK, "the current password unlocks the ceremony");
            string creationOptions = await begin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using JsonDocument options = JsonDocument.Parse(creationOptions);
            options.RootElement.GetProperty("rp").GetProperty("id").GetString().Should().Be(RelyingPartyId);
            options.RootElement.GetProperty("user").GetProperty("name").GetString().Should().Be(user.UserName);

            using FormUrlEncodedContent registerBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Name"] = "MacBook",
                [PasskeyCredential.FormField] = authenticator.Attest(creationOptions),
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
        HttpResponseMessage challenge = await anonymous.PostAsync($"/Account/Login?handler={LoginModel.PasskeyOptionsHandler}", challengeBody, TestContext.Current.CancellationToken);
        string requestOptions = await challenge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent assertionBody = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            [PasskeyCredential.FormField] = authenticator.Assert(requestOptions, user.Id.ToString(CultureInfo.InvariantCulture)),
            ["rememberMe"] = "false",
        });

        HttpResponseMessage signedIn = await anonymous.PostAsync("/Account/Login?handler=Passkey", assertionBody, TestContext.Current.CancellationToken);

        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "the passkey just added is a complete sign-in");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, signedIn).Should().BeTrue();
    }

    /// <summary>
    /// The administrator's recovery path, end to end: the owner's passkey is listed on their detail
    /// page and revoked from it - on the administrator's own password, which the page asks for
    /// before it does anything.
    /// </summary>
    [Fact]
    public async Task AnAdministratorSeesAnAccountsPasskeyAndRevokesOne()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        ownerClient.Dispose();
        UserPasskeyInfo passkey = await SeedPasskeyAsync(owner, "phone");

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "admin@example.com", AdminBootstrap.AdminRole);
        string detailPath = $"{AdminPath}/Detail/{owner.Id.ToString(CultureInfo.InvariantCulture)}";

        using (admin)
        {
            await EnrolmentFlowHelper.ElevateAsync(admin);

            HttpResponseMessage roster = await admin.GetAsync($"{AdminPath}/Index", TestContext.Current.CancellationToken);
            string rosterHtml = await roster.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            HttpResponseMessage before = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);
            string beforeHtml = await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(beforeHtml);

            using FormUrlEncodedContent revokeBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["credentialId"] = Convert.ToBase64String(passkey.CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            });

            // Act
            // Posted to the URL the rendered button carries, not to a path composed here: a handler
            // name that resolves to nothing still renders a formaction, so a test that composes its
            // own URL cannot tell a working button from a dead one.
            HttpResponseMessage revoked = await admin.PostAsync(ButtonTarget(beforeHtml, "RevokePasskey"), revokeBody, TestContext.Current.CancellationToken);
            HttpResponseMessage after = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);
            string afterHtml = await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            roster.StatusCode.Should().Be(HttpStatusCode.OK);
            rosterHtml.Should().Contain(owner.UserName!);
            before.StatusCode.Should().Be(HttpStatusCode.OK);
            beforeHtml.Should().Contain(owner.UserName!).And.Contain("phone");
            revoked.StatusCode.Should().Be(HttpStatusCode.Redirect);
            afterHtml.Should().Contain("Passkey revoked.").And.Contain("No passkeys enrolled.");
        }
    }

    /// <summary>
    /// A live administrator session alone reaches nothing: without an elevation the screens send the
    /// browser to the challenge, and a passkey posted at directly stays where it is.
    /// </summary>
    [Fact]
    public async Task AnAdministratorWhoHasNotConfirmedReachesNothing()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        ownerClient.Dispose();
        UserPasskeyInfo passkey = await SeedPasskeyAsync(owner, "phone");

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "admin@example.com", AdminBootstrap.AdminRole);
        string detailPath = $"{AdminPath}/Detail/{owner.Id.ToString(CultureInfo.InvariantCulture)}";

        using (admin)
        {
            // Act
            HttpResponseMessage page = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);

            // The antiforgery token comes from the challenge it was sent to, so the post that follows
            // fails on the elevation rather than on a missing token.
            HttpResponseMessage challenge = await admin.GetAsync("/Admin/Challenge", TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(
                await challenge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            using FormUrlEncodedContent revokeBody = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["credentialId"] = Convert.ToBase64String(passkey.CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            });

            HttpResponseMessage posted = await admin.PostAsync(
                $"{detailPath}?handler=RevokePasskey", revokeBody, TestContext.Current.CancellationToken);

            // Assert
            page.StatusCode.Should().Be(HttpStatusCode.Redirect, "an unelevated administrator is asked to confirm");
            page.Headers.Location!.OriginalString.Should().Contain("/Admin/Challenge");
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            posted.Headers.Location!.OriginalString.Should().Contain("/Admin/Challenge");
        }

        (await PasskeysOfAsync(owner)).Should().ContainSingle("nothing was revoked");
    }

    [Fact]
    public async Task ANonAdministratorCannotReachTheAdminScreen()
    {
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");

        using (client)
        {
            HttpResponseMessage page = await client.GetAsync($"{AdminPath}/Index", TestContext.Current.CancellationToken);

            page.StatusCode.Should().NotBe(HttpStatusCode.OK, "the screen lists other people's credentials");
        }
    }

    /// <summary>
    /// Where the button whose handler is <paramref name="handler"/> actually posts, read off the
    /// rendered page.
    /// </summary>
    private static string ButtonTarget(string html, string handler)
    {
        Match match = Regex.Match(html, $"formaction=\"(?<url>[^\"]*handler={Regex.Escape(handler)})\"");

        match.Success.Should().BeTrue($"the page must render a button posting to the {handler} handler");

        return WebUtility.HtmlDecode(match.Groups["url"].Value);
    }

    /// <summary>The passkeys the store holds for <paramref name="user"/> right now.</summary>
    private async Task<IList<UserPasskeyInfo>> PasskeysOfAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser tracked = (await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;

        return await users.GetPasskeysAsync(tracked);
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
