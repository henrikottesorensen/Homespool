using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
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
/// <b>Driven end to end because every step is a different trust boundary</b> - an elevated
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
            await EnrolmentFlowHelper.ElevateAsync(admin);

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

    /// <summary>
    /// A recovery is a proved password, not a way past a second factor: an account holding an
    /// authenticator is still asked for the code before it gets a session.
    /// </summary>
    /// <remarks>
    /// Ported from the adoption tests when that path was removed. The behaviour is unchanged and
    /// still worth pinning - both paths end at the same pre-sign-in check, and a recovery that signed
    /// somebody straight in would be the interesting bug.
    /// </remarks>
    [Fact]
    public async Task ARecoveredAccountWithAnAuthenticatorIsStillAskedForTheCode()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser tracked = (await users.FindByIdAsync(subject.Id.ToString(CultureInfo.InvariantCulture)))!;

            (await users.ResetAuthenticatorKeyAsync(tracked)).Succeeded.Should().BeTrue();
            (await users.SetTwoFactorEnabledAsync(tracked, true)).Succeeded.Should().BeTrue();
        }

        string link = await IssueRecoveryAsync(subject, clearAuthenticator: false);

        // Act
        HttpResponseMessage redeemed = await RedeemAsync(link);

        // Assert
        redeemed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redeemed.Headers.Location!.OriginalString.Should().StartWith("/Account/LoginWith2fa", "the code is still owed");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, redeemed).Should()
            .BeFalse("no session until the code is answered");
    }

    /// <summary>
    /// An unconfirmed account is recovered and then held at confirmation, as a new account is: an
    /// invite is not the same proof as answering mail sent to the address.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedAccountIsRecoveredAndHeldAtConfirmation()
    {
        // Arrange
        (HSUser subject, HttpClient subjectClient) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "subject@example.com");
        subjectClient.Dispose();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser tracked = (await users.FindByIdAsync(subject.Id.ToString(CultureInfo.InvariantCulture)))!;

            tracked.EmailConfirmed = false;
            (await users.UpdateAsync(tracked)).Succeeded.Should().BeTrue();
        }

        string link = await IssueRecoveryAsync(subject, clearAuthenticator: false);

        // Act
        HttpResponseMessage redeemed = await RedeemAsync(link);

        // Assert
        redeemed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redeemed.Headers.Location!.OriginalString.Should().StartWith(
            "/Account/RegisterConfirmation", "the address has not answered its mail");
    }

    /// <summary>An administrator who has not confirmed at the challenge issues nothing.</summary>
    [Fact]
    public async Task AnUnelevatedAdministratorCannotIssueARecovery()
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
            HttpResponseMessage challenge = await admin.GetAsync("/Admin/Challenge", TestContext.Current.CancellationToken);
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
            issued.Headers.Location!.OriginalString.Should().Contain("/Admin/Challenge");
        }

        (await SignInAsync("subject@example.com", OldPassword)).Should().Be(
            HttpStatusCode.Redirect, "nothing about the account changed");
    }

    /// <summary>An elevated administrator issues a recovery for <paramref name="subject"/>.</summary>
    private async Task<string> IssueRecoveryAsync(HSUser subject, bool clearAuthenticator)
    {
        (_, HttpClient admin) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        string detailPath = $"/Admin/Users/Detail/{subject.Id.ToString(CultureInfo.InvariantCulture)}";

        using (admin)
        {
            await EnrolmentFlowHelper.ElevateAsync(admin);

            HttpResponseMessage page = await admin.GetAsync(detailPath, TestContext.Current.CancellationToken);
            string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent issue = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
                ["ClearAuthenticator"] = clearAuthenticator ? "true" : "false",
            });

            HttpResponseMessage issued =
                await admin.PostAsync($"{detailPath}?handler=Recover", issue, TestContext.Current.CancellationToken);

            return RecoveryLink(await issued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Redeems a recovery link as somebody holding only the link.</summary>
    private async Task<HttpResponseMessage> RedeemAsync(string link)
    {
        using HttpClient holder = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage form = await holder.GetAsync(link, TestContext.Current.CancellationToken);
        string html = await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent redeem = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(html),
            ["Input.Password"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword,
        });

        return await holder.PostAsync(link, redeem, TestContext.Current.CancellationToken);
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
