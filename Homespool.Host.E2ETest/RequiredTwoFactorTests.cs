using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>Security:RequireTwoFactor</c>: an account with no authenticator is held on the enrolment page,
/// and the tokens it holds stop working.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a requirement on the account rather than the session</b> (Henrik, 2026-08-22), which is
/// the decision these pin. The browser half is the obvious one; the token half is the one with
/// consequences, because it means turning the setting on breaks integrations belonging to accounts
/// that have not enrolled. That is deliberate — a token minted before the setting existed would
/// otherwise outlive the requirement — and it is exactly the kind of thing that gets softened later by
/// somebody who meets it as a support call rather than as a decision.
/// </para>
/// <para>
/// <b>What must stay untouched is printers.</b> A machine has no second factor, so the gate keys on
/// the application cookie rather than on paths; the printer case is covered where the printer suites
/// already live, and the unit test for the scheme check is in <c>Homespool.Host.Test</c>.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class RequiredTwoFactorTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string Address = "no-authenticator@example.com";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-require2fa-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
        _factory.ConfigurationOverrides["Security:RequireTwoFactor"] = "true";

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>An ordinary page is withheld until the account has an authenticator.</summary>
    [Fact]
    public async Task AnAccountWithNoAuthenticatorIsSentToEnrol()
    {
        using HttpClient client = await SignedInAsync();

        using HttpResponseMessage response = await client.GetAsync("/Printers", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("EnableAuthenticator");
    }

    /// <summary>
    /// The enrolment page itself, and the one render of the recovery codes, stay reachable — or the
    /// gate would hold an account somewhere it cannot leave.
    /// </summary>
    /// <remarks>
    /// <b>"Was not sent to enrol", not "did not redirect".</b> Two of these three redirect for reasons
    /// of their own and always did: <c>Logout</c> lands on the home page, and <c>ShowRecoveryCodes</c>
    /// sends a reader holding no codes to <c>TwoFactorAuthentication</c> rather than drawing an empty
    /// list. Asserting on the absence of a redirect failed against correct
    /// behaviour and would have been "fixed" by exempting less.
    /// </remarks>
    [Theory]
    [InlineData("/Account/Manage/EnableAuthenticator")]
    [InlineData("/Account/Manage/ShowRecoveryCodes")]
    [InlineData("/Account/Logout")]
    public async Task TheWayOutStaysOpen(string path)
    {
        using HttpClient client = await SignedInAsync();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        string destination = response.Headers.Location?.OriginalString ?? string.Empty;

        destination.Should().NotContain("EnableAuthenticator",
                                        "{0} is how an account satisfies the requirement or leaves, so the gate "
                                        + "must not bounce it back to enrolment", path);
    }

    /// <summary>A signed-in browser call to the API is refused rather than redirected to a page.</summary>
    /// <remarks>
    /// A redirect to HTML arrives at a script as a 200 with a login form in it, which is the reasoning
    /// <c>ApiStatusCodeCookieEvents</c> already applies to the sign-in redirect for this same caller.
    /// </remarks>
    [Fact]
    public async Task AnApiCallIsRefusedRatherThanRedirected()
    {
        using HttpClient client = await SignedInAsync();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The token half: a personal access token belonging to an account with no authenticator stops
    /// working, which is what makes this a requirement on the account.
    /// </summary>
    [Fact]
    public async Task ATokenFromAnAccountWithNoAuthenticatorIsRefused()
    {
        string token = await MintTokenAsync();

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                                        "the requirement reaches every credential the account holds");
    }

    /// <summary>
    /// The same token, on a deployment that has not turned the setting on, works — without which the
    /// test above would pass just as well against a token path that was simply broken.
    /// </summary>
    [Fact]
    public async Task TheSameTokenWorksWhenTheSettingIsOff()
    {
        string token = await MintTokenAsync();

        using HomespoolFactory permissive = new($"Data Source={_databasePath}");

        using IServiceScope scope = permissive.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        using HttpClient client = permissive.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "nothing is wrong with the token itself");
    }

    private async Task<string> MintTokenAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        HSUser user = await CreateUserAsync(scope);

        (_, string plaintext) = await scope.ServiceProvider.GetRequiredService<ApiTokenService>()
            .CreateAsync(user.Id, "test", CapabilityPresets.Operator, TestContext.Current.CancellationToken);

        return plaintext;
    }

    private async Task<HttpClient> SignedInAsync()
    {
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            await CreateUserAsync(scope);
        }

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string page = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Login"] = Address,
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        using HttpResponseMessage signIn = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        // Signing in still works: the requirement is about what the account may then do, not about
        // refusing the credential it already has.
        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "an account with no authenticator can still sign in");

        return client;
    }

    private static async Task<HSUser> CreateUserAsync(IServiceScope scope)
    {
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser? existing = await users.FindByEmailAsync(Address);

        if (existing is not null)
        {
            return existing;
        }

        IUserStore<HSUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();

        HSUser user = new();
        await store.SetUserNameAsync(user, "noauth", CancellationToken.None);
        await ((IUserEmailStore<HSUser>)store).SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = true;

        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();

        return user;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }
    }
}
