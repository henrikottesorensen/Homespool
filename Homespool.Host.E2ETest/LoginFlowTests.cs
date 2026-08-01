using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Drives the real <c>/Account/Login</c> page over HTTP - antiforgery token and all - the same way
/// <see cref="SetupFlowTests"/> drives <c>/setup</c>. Unlike every other Identity page in this
/// project, <c>LoginModel</c> had no test of any kind before this - not even a PageModel-level one
/// (contrast <c>RegisterModelTests</c> in <c>Homespool.Host.Test</c>, which calls
/// <c>RegisterModel.OnPostAsync</c> directly).
/// </summary>
[Collection("WebApplicationFactory")]
public sealed class LoginFlowTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-login-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    private static FormUrlEncodedContent LoginBody(string antiforgeryToken, string email, string password)
    {
        return new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });
    }

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
        _ = _factory.Server;

        // This suite isn't testing the setup gate - open it so /Account/Login is reachable, matching
        // EndToEndEnrolmentTests rather than SetupFlowTests' deliberately-incomplete setup.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    // CA1001 wants IDisposable on a type owning a disposable field even though xUnit's IAsyncLifetime
    // already drives cleanup via DisposeAsync above; WebApplicationFactory.Dispose is idempotent, so
    // this is a safe, redundant satisfier rather than a second real teardown path.
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

    /// <summary>
    /// Seeds an account directly via <see cref="UserManager{TUser}"/>, bypassing Register's own
    /// (separately untested-at-HTTP-level, out of scope here) form - this suite exists to verify
    /// Login, not account creation. <paramref name="confirmed"/> controls
    /// <see cref="IdentityUser{TKey}.EmailConfirmed"/> directly, rather than going through
    /// <c>AccountConfirmationPolicy</c>, so the unconfirmed-account test doesn't depend on the test
    /// factory's SMTP configuration to produce an unconfirmed account.
    /// </summary>
    private async Task CreateUserAsync(string email, bool confirmed)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, email, CancellationToken.None);
        await emailStore.SetEmailAsync(user, email, CancellationToken.None);
        user.EmailConfirmed = confirmed;

        IdentityResult result = await userManager.CreateAsync(user, Password);
        result.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");
    }

    /// <summary>The full happy path: correct credentials sign the user in and redirect to the app root.</summary>
    [Fact]
    public async Task PostingCorrectCredentialsSignsTheUserInAndRedirects()
    {
        // Arrange
        await CreateUserAsync("user@example.com", confirmed: true);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage getResponse = await client.GetAsync("/Account/Login");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

        using FormUrlEncodedContent body = LoginBody(antiforgeryToken, "user@example.com", Password);

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/Account/Login", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful login redirects to the return URL");
        postResponse.Headers.Location!.OriginalString.Should().Be("/");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, postResponse).Should().BeTrue("signing in issues the Identity application cookie");
    }

    /// <summary>A wrong password is rejected without signing anyone in.</summary>
    [Fact]
    public async Task PostingTheWrongPasswordIsRejectedWithoutSigningIn()
    {
        // Arrange
        await CreateUserAsync("user@example.com", confirmed: true);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage getResponse = await client.GetAsync("/Account/Login");
        string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

        using FormUrlEncodedContent body = LoginBody(antiforgeryToken, "user@example.com", "wrong-password");

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/Account/Login", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a rejected login re-renders the page rather than redirecting");
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, postResponse).Should().BeFalse("a rejected login must not sign anyone in");

        string html = await postResponse.Content.ReadAsStringAsync();
        html.Should().Contain("Invalid login attempt");
    }

    /// <summary>
    /// An unconfirmed account's *correct* password is rejected the same generic way as a wrong one -
    /// LoginModel has no branch for <c>SignInResult.NotAllowed</c>, so it falls through to the same
    /// "Invalid login attempt" message rather than revealing that the account exists but isn't
    /// confirmed yet.
    /// </summary>
    [Fact]
    public async Task PostingCorrectCredentialsForAnUnconfirmedAccountFailsTheSameWayAsAWrongPassword()
    {
        // Arrange
        await CreateUserAsync("unconfirmed@example.com", confirmed: false);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage getResponse = await client.GetAsync("/Account/Login");
        string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

        using FormUrlEncodedContent body = LoginBody(antiforgeryToken, "unconfirmed@example.com", Password);

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/Account/Login", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, postResponse).Should().BeFalse();

        string html = await postResponse.Content.ReadAsStringAsync();
        html.Should().Contain("Invalid login attempt");
    }

    /// <summary>
    /// A request missing the antiforgery token entirely is rejected before the handler even runs -
    /// proving the protection is real, not merely present in the markup.
    /// </summary>
    [Fact]
    public async Task PostingWithoutTheAntiforgeryTokenIsRejected()
    {
        // Arrange
        await CreateUserAsync("user@example.com", confirmed: true);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // A GET first, so the antiforgery cookie is present - only the form field is withheld, which
        // is exactly what a forged cross-site request would look like.
        await client.GetAsync("/Account/Login");

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Email"] = "user@example.com",
            ["Input.Password"] = Password,
        });

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/Account/Login", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, postResponse).Should().BeFalse();
    }

    /// <summary>
    /// Repeated wrong passwords eventually lock the account, rather than letting an attacker guess
    /// forever. Identity's lockout has always existed but was unreachable: the scaffolded
    /// <c>PasswordSignInAsync</c> call passed <c>lockoutOnFailure: false</c>, so
    /// <c>LoginModel</c>'s <c>IsLockedOut</c> branch and the whole <c>Lockout</c> page were dead code.
    /// </summary>
    /// <remarks>
    /// This is the internet-exposure case: people expose self-hosted printer servers whatever the
    /// documentation advises (OctoPrint's mass exposure is the precedent), and there is no rate
    /// limiting on the login form - so without lockout a known email plus Identity's 6-character
    /// minimum password was an unbounded guessing target. Loops past Identity's default
    /// MaxFailedAccessAttempts with headroom rather than hardcoding 5, so a changed default does not
    /// silently make this test meaningless.
    /// </remarks>
    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAccountOut()
    {
        // Arrange
        await CreateUserAsync("locked@example.com", confirmed: true);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage? lastResponse = null;

        // Act - wrong passwords until the lockout redirect appears.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            HttpResponseMessage getResponse = await client.GetAsync("/Account/Login");
            string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

            using FormUrlEncodedContent body = LoginBody(antiforgeryToken, "locked@example.com", "wrong-password");
            lastResponse = await client.PostAsync("/Account/Login", body);

            if (lastResponse.StatusCode == HttpStatusCode.Redirect
                && lastResponse.Headers.Location?.OriginalString.Contains("Lockout", StringComparison.OrdinalIgnoreCase) == true)
            {
                break;
            }
        }

        // Assert
        lastResponse!.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "enough wrong passwords must stop being merely rejected and lock the account");
        lastResponse.Headers.Location!.OriginalString.Should().Contain("Lockout");

        // And the lockout is real, not just a redirect: the *correct* password is refused too.
        HttpResponseMessage correctGet = await client.GetAsync("/Account/Login");
        string correctToken = AntiforgeryTestHelper.ExtractToken(await correctGet.Content.ReadAsStringAsync());
        using FormUrlEncodedContent correctBody = LoginBody(correctToken, "locked@example.com", Password);

        HttpResponseMessage correctResponse = await client.PostAsync("/Account/Login", correctBody);

        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, correctResponse)
            .Should().BeFalse("a locked-out account must not sign in even with the right password");
    }
}
