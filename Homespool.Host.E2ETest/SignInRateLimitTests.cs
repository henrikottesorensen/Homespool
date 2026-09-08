using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The per-address ceiling on the credential pages, driven over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this proves that the unit tests cannot is the wiring.</b> The rule and the pages carrying
/// it are two things, and a policy nothing names limits nothing while every assertion about the rule
/// itself stays green.
/// </para>
/// <para>
/// A proxy is named here because the limiter is off without one. Nothing else about this host is
/// proxied - the address stays absent, so every request shares the one window, which is what makes
/// the count observable from a single client at all.
/// </para>
/// </remarks>
public sealed class SignInRateLimitTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("sign-in-limit");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _factory.ConfigurationOverrides["XForwarded:KnownProxies:0"] = "172.28.0.2";
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

    /// <summary>
    /// A password guess costs a permit, and the page it is typed on goes on rendering after the
    /// window is spent - a flood must not take sign-in away from everybody else.
    /// </summary>
    [Fact]
    public async Task TheSignInPostIsRefusedOnceTheWindowIsSpentWhileThePageStillRenders()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        List<HttpStatusCode> answers = [];

        // Act
        for (int attempt = 0; attempt <= SignInRateLimit.PermitLimit; attempt += 1)
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Login"] = "nobody",
                ["Input.Password"] = "not-the-password",
            });

            HttpResponseMessage answer = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);
            answers.Add(answer.StatusCode);
        }

        HttpResponseMessage pageAfter = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        // Assert
        answers[..SignInRateLimit.PermitLimit].Should()
               .AllSatisfy(status => status.Should().NotBe(HttpStatusCode.TooManyRequests, "the window admits its permits"));
        answers[SignInRateLimit.PermitLimit].Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");
        pageAfter.StatusCode.Should().Be(HttpStatusCode.OK, "the limit is on the attempt, not on the page");
    }

    /// <summary>
    /// The budget is the address's, not the form's: spending it on one covered page refuses the next,
    /// because what is being bounded is the work an address can ask for and every one of these pages
    /// asks for the same work.
    /// </summary>
    [Fact]
    public async Task TheWindowIsSharedAcrossTheCoveredPages()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage page = await client.GetAsync("/Account/ForgotPassword", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        HttpStatusCode last = HttpStatusCode.OK;

        // Act
        for (int attempt = 0; attempt <= SignInRateLimit.PermitLimit; attempt += 1)
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Email"] = "nobody@homespool.example.net",
            });

            HttpResponseMessage answer = await client.PostAsync("/Account/ForgotPassword", body, TestContext.Current.CancellationToken);
            last = answer.StatusCode;
        }

        HttpResponseMessage otherPage = await client.GetAsync("/Account/ResendEmailConfirmation", TestContext.Current.CancellationToken);
        string otherToken = AntiforgeryTestHelper.ExtractToken(await otherPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent otherBody = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = otherToken,
            ["Input.Email"] = "nobody@homespool.example.net",
        });

        HttpResponseMessage otherPost = await client.PostAsync("/Account/ResendEmailConfirmation", otherBody, TestContext.Current.CancellationToken);

        // Assert
        last.Should().Be(HttpStatusCode.TooManyRequests, "the window is spent");
        otherPage.StatusCode.Should().Be(HttpStatusCode.OK, "a spent window still renders every page");
        otherPost.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the budget is the address's, not the form's");
    }
}
