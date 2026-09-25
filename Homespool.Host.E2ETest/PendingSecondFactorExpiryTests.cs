using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using OtpNet;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// How long a passed first factor stays passed: five minutes from the password, however often the
/// second-factor page is read in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Over HTTP because the renewal is only visible there.</b> A sliding cookie is reissued as the
/// response starts, and a bare request context never starts one.
/// </para>
/// <para>
/// The host's clock is a fake the test moves; the authenticator code is computed and checked on the
/// real one, so it stays current while the cookie's clock runs ahead.
/// </para>
/// </remarks>
public sealed class PendingSecondFactorExpiryTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";
    private const string Email = "pending@example.com";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("pending-2fa");

    // Started at the real time: the whole host reads this clock.
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

    [Fact]
    public async Task ReadingTheCodePageDoesNotStretchThePendingSignInPastFiveMinutes()
    {
        // Arrange
        byte[] secret = await CreateTwoFactorAccountAsync();
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await PassThePasswordAsync(client);

        // Act: past the half-life a sliding cookie is reissued for another five minutes.
        _clock.Advance(TimeSpan.FromMinutes(3));
        string token = await CodePageTokenAsync(client);
        _clock.Advance(TimeSpan.FromMinutes(3));
        HttpResponseMessage answer = await PostCodeAsync(client, token, new Totp(secret).ComputeTotp());

        // Assert
        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        answer.Headers.Location!.OriginalString.Should().Be("/Account/Login", "six minutes after the password, nothing is pending, so the sign-in starts again");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, answer).Should().BeFalse();
    }

    [Fact]
    public async Task ARightCodeWithinFiveMinutesOfThePasswordSignsIn()
    {
        // Arrange
        byte[] secret = await CreateTwoFactorAccountAsync();
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await PassThePasswordAsync(client);

        // Act
        _clock.Advance(TimeSpan.FromMinutes(3));
        string token = await CodePageTokenAsync(client);
        _clock.Advance(TimeSpan.FromMinutes(1));
        HttpResponseMessage answer = await PostCodeAsync(client, token, new Totp(secret).ComputeTotp());

        // Assert
        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        answer.Headers.Location!.OriginalString.Should().Be("/");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, answer).Should().BeTrue();
    }

    /// <summary>A confirmed account with a password and an authenticator; returns the authenticator's secret.</summary>
    private async Task<byte[]> CreateTwoFactorAccountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new(EnrolmentFlowHelper.UsernameFor(Email)) { Email = Email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();

        return Base32Encoding.ToBytes(await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException("the key was just set"));
    }

    private static async Task PassThePasswordAsync(HttpClient client)
    {
        HttpResponseMessage page = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Login"] = Email,
            ["Input.Password"] = Password,
        });
        HttpResponseMessage answer = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        answer.Headers.Location!.OriginalString.Should().StartWith("/Account/LoginWith2fa", "the password is right and a code is owed");
    }

    private static async Task<string> CodePageTokenAsync(HttpClient client)
    {
        HttpResponseMessage page = await client.GetAsync("/Account/LoginWith2fa", TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK, "the first factor is still pending when the page is read");

        return AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> PostCodeAsync(HttpClient client, string token, string code)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["RememberMe"] = "false",
            ["Input.TwoFactorCode"] = code,
        });

        return await client.PostAsync("/Account/LoginWith2fa", body, CancellationToken.None);
    }
}
