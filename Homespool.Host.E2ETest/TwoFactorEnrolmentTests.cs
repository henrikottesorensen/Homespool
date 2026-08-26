using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OtpNet;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Turning an authenticator app on, and re-keying one, driven through the real form posts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written after the enable flow was found to answer 500.</b> Both callers that mint recovery
/// codes redirected to a <c>ShowRecoveryCodes</c> page that did not exist - it was Identity.UI's, and
/// was never brought across when that package was removed. The account was left with two-factor on
/// and ten recovery codes it had never been shown, which is the one state
/// <c>EnableAuthenticatorModel</c>'s transaction exists to prevent the database half of.
/// </para>
/// <para>
/// <b>Nothing caught it, and the reason is worth keeping.</b> <see cref="LoginWith2faTests"/> seeds
/// two-factor through <see cref="UserManager{TUser}"/> directly and says so - <i>"this suite verifies
/// login, not enrolment"</i> - which is a reasonable shortcut that happens to step over the only code
/// path that was broken. So these tests deliberately use the form posts a person uses, antiforgery
/// token and all, rather than the manager underneath them.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class TwoFactorEnrolmentTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-2fa-enrol-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The whole point: a person who turns on an authenticator app is shown the recovery codes that
    /// were minted for them. They are stored hashed, so this request is the only chance they get.
    /// </summary>
    [Fact]
    public async Task EnablingAnAuthenticatorShowsTheRecoveryCodes()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("enable@example.com");

        using HttpClient client = CreateClient();

        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/EnableAuthenticator");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/EnableAuthenticator", new()
        {
            ["Input.Code"] = await CurrentCodeAsync(user.Id),
            ["__RequestVerificationToken"] = token,
        });

        post.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                    "a verified code enables two-factor and hands the codes on - a 500 here means the "
                                    + "page it hands them to is missing, which is exactly what this test was written for");

        post.Headers.Location!.OriginalString
            .Should().Contain("/Account/Manage/ShowRecoveryCodes");

        using HttpResponseMessage codes = await GetAsync(client, jar, post.Headers.Location.OriginalString);
        string html = await codes.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        codes.StatusCode.Should().Be(HttpStatusCode.OK);
        CountOccurrences(html, "recovery-code")
            .Should().Be(10, "ten codes are generated, and a code that is generated but not displayed is lost");
    }

    /// <summary>
    /// Re-keying is the remedy for a device that is gone or no longer trusted. It has to actually
    /// change the key, and it has to leave two-factor off - the flag is enabled against the old
    /// secret, so keeping it on would demand a code nothing could produce.
    /// </summary>
    [Fact]
    public async Task ResettingTheAuthenticatorKeyTurnsTwoFactorOffAndRekeys()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("reset@example.com", withTwoFactor: true);

        string keyBefore = await AuthenticatorKeyAsync(user.Id);

        using HttpClient client = CreateClient();

        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/ResetAuthenticator");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/ResetAuthenticator", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.OriginalString
            .Should().Contain("/Account/Manage/EnableAuthenticator",
                              "the window with two-factor off is closed by setting the app up again, so that is where "
                              + "the reader is put");

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser after = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture))
                       ?? throw new InvalidOperationException("the account should still exist");

        (await userManager.GetAuthenticatorKeyAsync(after))
            .Should().NotBe(keyBefore, "a reset that leaves the old secret working is not a reset");

        (await userManager.GetTwoFactorEnabledAsync(after))
            .Should().BeFalse("the enabled flag is enabled against the key that was just thrown away");
    }

    /// <summary>
    /// A refresh, a back button or a direct visit has no codes to show - and must not render an empty
    /// page that reads as though the codes were lost.
    /// </summary>
    [Fact]
    public async Task RecoveryCodesAreNotShownToAReaderWhoHasNone()
    {
        (HSUser _, CookieJar jar) = await SeedAsync("norecovery@example.com");

        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await GetAsync(client, jar, "/Account/Manage/ShowRecoveryCodes");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Manage/TwoFactorAuthentication");
    }

    /// <summary>
    /// The button that started this: an <c>asp-page</c> naming a page that does not exist renders as
    /// <c>href=""</c> rather than failing, so it reloads the page it is on and looks like nothing
    /// happened. A rendered link is not evidence of a reachable one.
    /// </summary>
    [Fact]
    public async Task TheTwoFactorPageOffersAResetLinkThatLeadsSomewhere()
    {
        (HSUser _, CookieJar jar) = await SeedAsync("resetlink@example.com", withTwoFactor: true);

        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await GetAsync(client, jar, "/Account/Manage/TwoFactorAuthentication");
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("id=\"reset-authenticator\"", "the button is only rendered once an app is configured");
        html.Should().NotContain("id=\"reset-authenticator\" class=\"btn btn-primary\" href=\"\"",
                                 "an empty href is what an unresolvable asp-page produces, and it is indistinguishable "
                                 + "from a working button until it is clicked");
    }

    /// <summary>
    /// An account with its cookie already in a jar, optionally with an authenticator configured and
    /// two-factor on.
    /// </summary>
    private async Task<(HSUser user, CookieJar jar)> SeedAsync(string email, bool withTwoFactor = false)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        CookieJar jar = new();
        jar.Seed(client.DefaultRequestHeaders.GetValues("Cookie").First());
        client.Dispose();

        if (withTwoFactor)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser fresh = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture))
                           ?? throw new InvalidOperationException("the account should exist");

            await userManager.ResetAuthenticatorKeyAsync(fresh);
            await userManager.SetTwoFactorEnabledAsync(fresh, true);
        }

        return (user, jar);
    }

    private async Task<string> AuthenticatorKeyAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture))
                      ?? throw new InvalidOperationException("the account should exist");

        return await userManager.GetAuthenticatorKeyAsync(user)
               ?? throw new InvalidOperationException("the account should have an authenticator key");
    }

    /// <summary>
    /// A code the seeded account's app would be showing right now, via Otp.NET for the reason
    /// <see cref="LoginWith2faTests"/> records: the authenticator provider only validates.
    /// </summary>
    private async Task<string> CurrentCodeAsync(long userId)
    {
        return new Totp(Base32Encoding.ToBytes(await AuthenticatorKeyAsync(userId))).ComputeTotp();
    }

    private HttpClient CreateClient()
    {
        // Cookies are carried by hand: the auth cookie is minted rather than obtained from a sign-in
        // post, and mixing a fixed Cookie header with the factory's own container sends two of them.
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
    }

    private async Task<HttpResponseMessage> GetAsync(HttpClient client, CookieJar jar, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        jar.Apply(request);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        jar.Capture(response);

        return response;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, CookieJar jar, string path, Dictionary<string, string> form)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Content = new FormUrlEncodedContent(form);
        jar.Apply(request);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        jar.Capture(response);

        return response;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, CookieJar jar, string path)
    {
        using HttpResponseMessage response = await GetAsync(client, jar, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form has to render before it can be posted");

        return AntiforgeryTestHelper.ExtractToken(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

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
    /// The few cookies these flows depend on - the sign-in ticket, the antiforgery pair and the one
    /// TempData rides in - carried across requests in the order a browser would.
    /// </summary>
    private sealed class CookieJar
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public void Seed(string headerValue)
        {
            foreach (string pair in headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Store(pair);
            }
        }

        public void Capture(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
            {
                return;
            }

            foreach (string value in values)
            {
                Store(value.Split(';')[0]);
            }
        }

        public void Apply(HttpRequestMessage request)
        {
            if (_cookies.Count == 0)
            {
                return;
            }

            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        private void Store(string pair)
        {
            int split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                return;
            }

            string name = pair[..split];
            string value = pair[(split + 1)..];

            // An expiry is how the framework deletes one - TempData's cookie goes this way once read.
            if (value.Length == 0)
            {
                _cookies.Remove(name);
                return;
            }

            _cookies[name] = value;
        }
    }
}
