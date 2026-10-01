using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Net.Http.Headers;

using Homespool.Host.Accounts;
using Homespool.Host.Middleware;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// How long a signed-in browser stays signed in without being used: the deployment's own cookie
/// lifetime when the person did not ask to be remembered, <see cref="SecurityOptions.RememberedSessionDays"/>
/// when they did.
/// </summary>
/// <remarks>
/// <b>Over HTTP because the renewal is only visible there.</b> A sliding cookie is reissued as the
/// response starts, and a bare request context never starts one - and the lifetime under test is the
/// one <c>Program</c> configures, which no unit rig reads.
/// </remarks>
public sealed class SessionLifetimeTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";
    private const string Email = "lifetime@example.com";

    private static readonly TimeSpan RememberedLifetime = TimeSpan.FromDays(new SecurityOptions().RememberedSessionDays);

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("session-lifetime");

    // Started at the real time: the whole host reads this clock, and the client's cookie container
    // judges a cookie's Expires on the real one.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        }));

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The phone opened every few days: a remembered sign-in is still signed in a week later, and past
    /// half its life the renewed cookie is good for the whole of it again.
    /// </summary>
    [Fact]
    public async Task ARememberedSignInSurvivesAWeekUnusedAndRenewsForItsWholeLifetime()
    {
        // Arrange
        await CreateAccountAsync();
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage signIn = await SignInAsync(client, rememberMe: true);
        DateTimeOffset signedIn = _clock.GetUtcNow();

        ApplicationCookieOf(signIn)!.Expires.Should().Be(Seconds(signedIn + RememberedLifetime));

        // Act
        _clock.Advance(TimeSpan.FromDays(7));
        HttpResponseMessage aWeekLater = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(10));
        HttpResponseMessage pastHalfLife = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);

        // Assert
        aWeekLater.StatusCode.Should().Be(HttpStatusCode.OK, "a week is well inside a remembered session's life");
        pastHalfLife.StatusCode.Should().Be(HttpStatusCode.OK);
        ApplicationCookieOf(pastHalfLife)!.Expires.Should().Be(Seconds(_clock.GetUtcNow() + RememberedLifetime),
                                                               "the renewal keeps the remembered length rather than falling back to the default");
    }

    [Fact]
    public async Task AnUnrememberedSignInIsABrowserSessionCookieAndEndsAfterTheConfiguredLifetime()
    {
        // Arrange
        await CreateAccountAsync();
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage signIn = await SignInAsync(client, rememberMe: false);

        ApplicationCookieOf(signIn)!.Expires.Should().BeNull("a browser drops a cookie without Expires when it closes");

        // Act
        _clock.Advance(ApplicationCookieOptions().ExpireTimeSpan + TimeSpan.FromMinutes(1));
        HttpResponseMessage later = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);

        // Assert
        later.StatusCode.Should().Be(HttpStatusCode.Redirect);
        later.Headers.Location!.OriginalString.Should().Contain("/Account/Login", "the session ended a day after it began");
    }

    private async Task CreateAccountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new(EnrolmentFlowHelper.UsernameFor(Email)) { Email = Email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
    }

    private async Task<HttpResponseMessage> SignInAsync(HttpClient client, bool rememberMe)
    {
        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Login"] = Email,
            ["Input.Password"] = Password,
            ["Input.RememberMe"] = rememberMe ? "true" : "false",
        });
        HttpResponseMessage answer = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, answer).Should().BeTrue();

        return answer;
    }

    private CookieAuthenticationOptions ApplicationCookieOptions()
    {
        return _factory.Services
                       .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                       .Get(IdentityConstants.ApplicationScheme);
    }

    /// <summary>The application cookie <paramref name="response"/> set, or null when it set none.</summary>
    private SetCookieHeaderValue? ApplicationCookieOf(HttpResponseMessage response)
    {
        string name = ApplicationCookieOptions().Cookie.Name!;

        return response.Headers.TryGetValues(HeaderNames.SetCookie, out IEnumerable<string>? values) ?
            SetCookieHeaderValue.ParseList(values.ToList()).FirstOrDefault(cookie => cookie.Name.Equals(name, StringComparison.Ordinal)) :
            null;
    }

    /// <summary><paramref name="time"/> to the whole second, as a cookie's <c>Expires</c> carries it.</summary>
    private static DateTimeOffset Seconds(DateTimeOffset time)
    {
        return new DateTimeOffset(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}
