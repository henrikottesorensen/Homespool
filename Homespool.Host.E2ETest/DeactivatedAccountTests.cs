using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Closing an account from the administrator's screen, and what it closes: the API token and the
/// browser already signed in both stop on their next request, and the password stops signing in.
/// </summary>
/// <remarks>
/// <b>Driven over HTTP because the value of the feature is at that seam.</b> The page-model tests
/// prove the act; this proves that the act reaches a credential presented by a real client - which
/// is the claim an operator is relying on when they close somebody's account.
/// </remarks>
public sealed class DeactivatedAccountTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("deactivated");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

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
    public async Task DeactivatingAnAccountStopsItsApiTokenItsSessionAndItsPassword()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        using HttpClient signedIn = subjectClient;

        string token = await MintTokenAsync(subject.Id);

        using HttpClient bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage before = await bearer.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);
        HttpResponseMessage sessionBefore = await signedIn.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Uuid}";

        // Act
        using (admin)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(admin);

            HttpResponseMessage page = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);
            string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent deactivate = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
            });

            HttpResponseMessage posted =
                await admin.PostAsync($"{detailPath}?handler=Deactivate", deactivate, TestContext.Current.CancellationToken);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        HttpResponseMessage after = await bearer.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);
        HttpResponseMessage sessionAfter = await signedIn.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        // Assert
        before.StatusCode.Should().Be(HttpStatusCode.OK, "the token worked before the account was closed");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the token is gone, and the account may not sign in either way");
        sessionBefore.StatusCode.Should().Be(HttpStatusCode.OK, "the browser was signed in before the account was closed");
        sessionAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "its session ended with the account, on its very next request");

        (await SignInAsync("subject@example.com")).Should().NotBe(
            HttpStatusCode.Redirect, "a closed account's password must not open a session");
    }

    /// <summary>
    /// Reopening lets the password back in - and does not bring the token back, which was deleted
    /// rather than suspended.
    /// </summary>
    [Fact]
    public async Task ReactivatingLetsTheAccountSignInAgain()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        string token = await MintTokenAsync(subject.Id);

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Uuid}";

        // Act
        using (admin)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(admin);

            await PostAsync(admin, detailPath, "Deactivate");
            await PostAsync(admin, detailPath, "Reactivate");
        }

        using HttpClient bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage withToken = await bearer.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        // Assert
        (await SignInAsync("subject@example.com")).Should().Be(HttpStatusCode.Redirect, "the account is open again");
        withToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "its tokens were revoked, not suspended");
    }

    /// <summary>
    /// An administrator closed by another, in the middle of using the page: every button on it is
    /// refused to the session they were signed in with - reopening themselves, reopening somebody
    /// else, and issuing a recovery for an open account - because that session ended with the
    /// account and the next request is sent to sign in. The row checks in the page's policy and the
    /// service behind it are proved by the unit tests.
    /// </summary>
    [Fact]
    public async Task AClosedAdministratorsSessionCanReopenNobody()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();
        (HSUser bystander, HttpClient bystanderClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "bystander@example.com");
        bystanderClient.Dispose();

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);
        (HSUser deputy, HttpClient deputyClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "deputy@example.com", AdminBootstrap.AdminRole);

        using (admin)
        using (deputyClient)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(admin);
            await EnrolmentFlowHelper.ReauthenticateAsync(deputyClient);

            // The token is taken while the deputy is still open: once closed, the page itself is
            // refused, and what is being proved is that the act is too.
            string token = await AntiforgeryTokenAsync(deputyClient, $"/Admin/Users/Detail/{deputy.Uuid}");

            await PostAsync(admin, $"/Admin/Users/Detail/{subject.Uuid}", "Deactivate");
            await PostAsync(admin, $"/Admin/Users/Detail/{deputy.Uuid}", "Deactivate");

            // Act
            HttpResponseMessage reopenSelf = await PostWithTokenAsync(deputyClient, $"/Admin/Users/Detail/{deputy.Uuid}", "Reactivate", token);
            HttpResponseMessage reopenOther = await PostWithTokenAsync(deputyClient, $"/Admin/Users/Detail/{subject.Uuid}", "Reactivate", token);
            HttpResponseMessage recover = await PostWithTokenAsync(deputyClient, $"/Admin/Users/Detail/{bystander.Uuid}", "Recover", token);

            // Assert
            foreach (HttpResponseMessage refused in new[] { reopenSelf, reopenOther, recover })
            {
                refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
                refused.Headers.Location!.ToString().Should().Contain("/Account/Login",
                    "a closed administrator's session ended with the account");
            }
        }

        (await SignInAsync("deputy@example.com")).Should().NotBe(HttpStatusCode.Redirect, "the closed administrator stays closed");
        (await SignInAsync("subject@example.com")).Should().NotBe(HttpStatusCode.Redirect, "and so does the account they aimed at");
    }

    /// <summary>
    /// The same, on every administration page rather than one button: the closed administrator's
    /// cookie still carries the role, and no page is served to it. The open administrator is the
    /// control, so a refusal is the closure and not the pages.
    /// </summary>
    [Fact]
    public async Task AClosedAdministratorsSessionLosesEveryAdministrationPage()
    {
        // Arrange
        string[] pages =
        [
            "/Admin/Settings",
            "/Admin/Certificate",
            "/Admin/LiveView",
            "/Admin/Invites",
            "/Admin/Invites/Create",
            "/Admin/Users",
        ];

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);
        (HSUser deputy, HttpClient deputyClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "deputy@example.com", AdminBootstrap.AdminRole);

        using (admin)
        using (deputyClient)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(admin);
            await EnrolmentFlowHelper.ReauthenticateAsync(deputyClient);

            foreach (string page in pages)
            {
                (await deputyClient.GetAsync(page, TestContext.Current.CancellationToken)).StatusCode
                    .Should().Be(HttpStatusCode.OK, $"{page} is the open deputy's to read before the closure");
            }

            await PostAsync(admin, $"/Admin/Users/Detail/{deputy.Uuid}", "Deactivate");

            // Act
            foreach (string page in pages)
            {
                HttpResponseMessage closed = await deputyClient.GetAsync(page, TestContext.Current.CancellationToken);
                HttpResponseMessage open = await admin.GetAsync(page, TestContext.Current.CancellationToken);

                // Assert
                closed.StatusCode.Should().Be(HttpStatusCode.Redirect, $"{page} must refuse the closed administrator's cookie");
                closed.Headers.Location!.ToString().Should().Contain("/Account/Login", $"{page} finds the session ended, rather than asking for proof");
                open.StatusCode.Should().Be(HttpStatusCode.OK, $"{page} still serves the administrator who is open");
            }
        }
    }

    private async Task PostAsync(HttpClient client, string path, string handler)
    {
        HttpResponseMessage posted = await PostForResponseAsync(client, path, handler);

        posted.StatusCode.Should().Be(HttpStatusCode.Redirect, "the act was accepted");
        posted.Headers.Location!.ToString().Should().NotContain("/Account/AccessDenied", "the act was accepted");
    }

    private async Task<HttpResponseMessage> PostForResponseAsync(HttpClient client, string path, string handler)
    {
        return await PostWithTokenAsync(client, path, handler, await AntiforgeryTokenAsync(client, path));
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string path, string handler, string token)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        });

        return await client.PostAsync($"{path}?handler={handler}", body, TestContext.Current.CancellationToken);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        HttpResponseMessage page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return AntiforgeryTestHelper.ExtractToken(html);
    }

    private async Task<string> MintTokenAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
        (_, string plaintext) = await tokens.CreateAsync(userId, "laptop", CapabilitySet.Everything, CancellationToken.None);

        return plaintext;
    }

    /// <summary>The status the login form answers with for this account and its own password.</summary>
    private async Task<HttpStatusCode> SignInAsync(string email)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage form = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string html = await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
            ["Input.Login"] = email,
            ["Input.Password"] = Password,
        });

        HttpResponseMessage posted = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        return posted.StatusCode;
    }
}
