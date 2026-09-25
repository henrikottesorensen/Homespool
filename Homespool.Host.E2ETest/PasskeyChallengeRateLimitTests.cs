using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The per-address ceiling on passkey challenges, driven over HTTP: the login page's challenge
/// handler answers until the window is spent and 429 after, while the page's other handlers go on
/// answering. Its own fixture, so the window it spends is nobody else's.
/// </summary>
/// <remarks>
/// Passkeys are not configured here, so a challenge answers 404 - withheld - rather than 200. That
/// is the point being made: the limiter runs before the endpoint, and a request that would be
/// refused anyway still spends a permit, so the count is of requests and not of ceremonies.
/// </remarks>
public sealed class PasskeyChallengeRateLimitTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("passkey-limit");
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
    public async Task TheChallengeIsRefusedOnceTheWindowIsSpentWhileTheRestOfThePageIsNot()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage loginGet = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await loginGet.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        List<HttpStatusCode> answers = [];

        // Act
        for (int i = 0; i <= PasskeyChallengeRateLimit.PermitLimit; i += 1)
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            HttpResponseMessage challenge = await client.PostAsync($"/Account/Login?handler={LoginModel.PasskeyOptionsHandler}", body, TestContext.Current.CancellationToken);
            answers.Add(challenge.StatusCode);
        }

        HttpResponseMessage pageAfter = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        // Assert
        answers[..PasskeyChallengeRateLimit.PermitLimit].Should().AllSatisfy(status => status.Should().NotBe(HttpStatusCode.TooManyRequests, "the window admits its permits"));
        answers[PasskeyChallengeRateLimit.PermitLimit].Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");
        pageAfter.StatusCode.Should().Be(HttpStatusCode.OK, "the limit is on the challenge, not on the page");
    }

    /// <summary>
    /// Razor Pages runs the handler the <b>first</b> <c>handler</c> value names, so a second value after
    /// it changes nothing about what runs - and must change nothing about which window it spends. No
    /// proxy is trusted here, so a challenge the limiter failed to recognise would fall to the page's
    /// credential policy, which declines to limit, and answer as often as it was asked.
    /// </summary>
    [Fact]
    public async Task AChallengeWithADuplicatedHandlerParameterSpendsTheSameWindow()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage loginGet = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await loginGet.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        List<HttpStatusCode> answers = [];

        // Act
        for (int i = 0; i <= PasskeyChallengeRateLimit.PermitLimit; i += 1)
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            HttpResponseMessage challenge = await client.PostAsync($"/Account/Login?handler={LoginModel.PasskeyOptionsHandler}&handler=Other",
                                                                   body,
                                                                   TestContext.Current.CancellationToken);
            answers.Add(challenge.StatusCode);
        }

        // Assert
        answers[0].Should().Be(HttpStatusCode.NotFound, "the first value names the handler that runs: the challenge, withheld with passkeys off");
        answers[PasskeyChallengeRateLimit.PermitLimit].Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");
    }

    /// <summary>
    /// The Manage page carries the challenge policy alone, so a registration challenge the limiter
    /// failed to recognise would have no limiter at all. The limiter runs before the page, so a
    /// request the page then refuses - no recent proof here - still spends its permit.
    /// </summary>
    [Fact]
    public async Task ARegistrationChallengeWithADuplicatedHandlerParameterSpendsTheSameWindow()
    {
        // Arrange
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        List<HttpStatusCode> answers = [];

        using (client)
        {
            HttpResponseMessage page = await client.GetAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);
            string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            // Act
            for (int i = 0; i <= PasskeyChallengeRateLimit.PermitLimit; i += 1)
            {
                using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
                HttpResponseMessage challenge = await client.PostAsync($"/Account/Manage/Passkeys?handler={PasskeysModel.BeginRegistrationHandler}&handler={PasskeysModel.BeginRegistrationHandler}",
                                                                       body,
                                                                       TestContext.Current.CancellationToken);
                answers.Add(challenge.StatusCode);
            }
        }

        // Assert
        answers[..PasskeyChallengeRateLimit.PermitLimit].Should().AllSatisfy(status => status.Should().NotBe(HttpStatusCode.TooManyRequests, "the window admits its permits"));
        answers[PasskeyChallengeRateLimit.PermitLimit].Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");
    }
}
