using System.Collections.Generic;
using System.Globalization;
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
/// Closing an account from the administrator's screen, and what it closes: the API token stops on
/// the next request, and the password stops signing in.
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
    public async Task DeactivatingAnAccountStopsItsApiTokenAndItsPassword()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        string token = await MintTokenAsync(subject.Id);

        using HttpClient bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage before = await bearer.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Id.ToString(CultureInfo.InvariantCulture)}";

        // Act
        using (admin)
        {
            await EnrolmentFlowHelper.ElevateAsync(admin);

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

        // Assert
        before.StatusCode.Should().Be(HttpStatusCode.OK, "the token worked before the account was closed");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the token is gone, and the account may not sign in either way");

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

        string detailPath = $"/Admin/Users/Detail/{subject.Id.ToString(CultureInfo.InvariantCulture)}";

        // Act
        using (admin)
        {
            await EnrolmentFlowHelper.ElevateAsync(admin);

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

    private async Task PostAsync(HttpClient client, string path, string handler)
    {
        HttpResponseMessage page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
        });

        HttpResponseMessage posted = await client.PostAsync($"{path}?handler={handler}", body, TestContext.Current.CancellationToken);

        posted.StatusCode.Should().Be(HttpStatusCode.Redirect, "the act was accepted");
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
