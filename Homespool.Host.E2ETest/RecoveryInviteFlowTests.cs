using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The whole recovery, over HTTP: an administrator issues a link for somebody who has lost their
/// credentials, and that person uses it to get back in.
/// </summary>
/// <remarks>
/// <b>Driven end to end because every step is a different trust boundary</b> - a proved
/// administrator issuing, an anonymous holder of a link redeeming, and the login form afterwards.
/// The page-model tests prove what redemption does; this proves the link an administrator is handed
/// is one that works.
/// </remarks>
public sealed class RecoveryInviteFlowTests : IAsyncLifetime
{
    private const string OldPassword = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string NewPassword = "Different-Horse-Battery-Staple-2!"; // betterleaks:allow

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("recovery");

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
    public async Task AnAdministratorIssuesARecoveryAndItsOwnerGetsBackIn()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Id.ToString(CultureInfo.InvariantCulture)}";
        string link;

        using (admin)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(admin);

            HttpResponseMessage page = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);
            string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent issue = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
                ["ClearAuthenticator"] = "true",
            });

            // Act - issue
            HttpResponseMessage issued =
                await admin.PostAsync($"{detailPath}?handler=Recover", issue, TestContext.Current.CancellationToken);

            string issuedHtml = await issued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            issued.StatusCode.Should().Be(HttpStatusCode.OK, "the link is rendered by the post that mints it");
            link = RecoveryLink(issuedHtml);
        }

        // Act - redeem, as somebody holding only the link
        using HttpClient holder = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage form = await holder.GetAsync(link, TestContext.Current.CancellationToken);
        string formHtml = await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent redeem = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(formHtml),
            ["Input.Password"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword,
        });

        HttpResponseMessage redeemed = await holder.PostAsync(link, redeem, TestContext.Current.CancellationToken);

        // Assert
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        formHtml.Should().Contain(subject.UserName!, "the page names whose account is being recovered");

        redeemed.StatusCode.Should().Be(HttpStatusCode.Redirect, "a redeemed recovery signs the owner in");

        (await SignInAsync("subject@example.com", NewPassword)).Should().Be(
            HttpStatusCode.Redirect, "the new password works");
        (await SignInAsync("subject@example.com", OldPassword)).Should().NotBe(
            HttpStatusCode.Redirect, "the old one does not");
    }

    /// <summary>An administrator who has not proved themselves issues nothing.</summary>
    [Fact]
    public async Task AnUnprovedAdministratorCannotIssueARecovery()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Id.ToString(CultureInfo.InvariantCulture)}";

        using (admin)
        {
            HttpResponseMessage challenge = await admin.GetAsync("/Account/Reauthenticate", TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(
                await challenge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            using FormUrlEncodedContent issue = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["ClearAuthenticator"] = "true",
            });

            // Act
            HttpResponseMessage issued =
                await admin.PostAsync($"{detailPath}?handler=Recover", issue, TestContext.Current.CancellationToken);

            // Assert
            issued.StatusCode.Should().Be(HttpStatusCode.Redirect);
            issued.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate");
        }

        (await SignInAsync("subject@example.com", OldPassword)).Should().Be(
            HttpStatusCode.Redirect, "nothing about the account changed");
    }

    /// <summary>The link the page rendered, read off the response that minted it.</summary>
    private static string RecoveryLink(string html)
    {
        Match match = Regex.Match(html, "value=\"(?<url>[^\"]*/Account/Register[^\"]*)\"");

        match.Success.Should().BeTrue("the page renders the recovery link once, in a readonly input");

        return WebUtility.HtmlDecode(match.Groups["url"].Value);
    }

    private async Task<HttpStatusCode> SignInAsync(string email, string password)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage form = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string html = await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
            ["Input.Login"] = email,
            ["Input.Password"] = password,
        });

        HttpResponseMessage posted = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        return posted.StatusCode;
    }
}
