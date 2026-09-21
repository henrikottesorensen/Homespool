using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OtpNet;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Turning an authenticator app on, and re-keying one, driven through the real form posts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The recovery codes are shown by the response to the post that mints them, and only there.</b>
/// They are stored hashed, so a set minted and not displayed is lost, and an account left with
/// two-factor on and codes it was never shown is the one state <c>EnableAuthenticatorModel</c>'s
/// transaction exists to prevent the database half of. They are not handed to another request
/// either: the <c>TempData</c> cookie a redirect would carry them in is not bound to the account and
/// stays decryptable after it has been read.
/// </para>
/// <para>
/// <b>Nothing caught it, and the reason is worth keeping.</b> <see cref="LoginWith2faTests"/> seeds
/// two-factor through <see cref="UserManager{TUser}"/> directly and says so - <i>"this suite verifies
/// login, not enrolment"</i> - which is a reasonable shortcut that happens to step over the only code
/// path that was broken. So these tests deliberately use the form posts a person uses, antiforgery
/// token and all, rather than the manager underneath them.
/// </para>
/// </remarks>
public sealed class TwoFactorEnrolmentTests : IAsyncLifetime
{
    private const string SeededPassword = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("2fa-enrol");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The whole point: a person who turns on an authenticator app is shown the recovery codes that
    /// were minted for them, in the response to that post, and in no cookie.
    /// </summary>
    [Fact]
    public async Task EnablingAnAuthenticatorShowsTheRecoveryCodes()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("enable@example.com");

        using HttpClient client = CreateClient();

        await ProveAsync(client, jar);
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/EnableAuthenticator");

        Dictionary<string, string> form = new()
        {
            ["Input.Code"] = await CurrentCodeAsync(user.Id),
            ["__RequestVerificationToken"] = token,
        };

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/EnableAuthenticator", form);

        await ShouldShowLiveCodesAndCarryNoneAsync(post, user.Id);

        // A refresh of that response is the same post again. The codes are only ever shown once, so it
        // must land on the two-factor page rather than mint or show a second set.
        using HttpResponseMessage refresh = await PostAsync(client, jar, "/Account/Manage/EnableAuthenticator", form);

        refresh.StatusCode.Should().Be(HttpStatusCode.Redirect);
        refresh.Headers.Location!.OriginalString.Should().Contain("/Account/Manage/TwoFactorAuthentication");
    }

    /// <summary>
    /// The other page that mints codes: a fresh set is shown by the post that generated it, and the
    /// set shown is the set stored.
    /// </summary>
    [Fact]
    public async Task GeneratingRecoveryCodesShowsThemInTheResponse()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("regenerate@example.com", withTwoFactor: true);

        using HttpClient client = CreateClient();

        await ProveAsync(client, jar);
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/GenerateRecoveryCodes");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/GenerateRecoveryCodes", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        await ShouldShowLiveCodesAndCarryNoneAsync(post, user.Id);
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

        await ProveAsync(client, jar);
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/ResetAuthenticator");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/ResetAuthenticator", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.OriginalString
            .Should().Contain("/Account/Manage/EnableAuthenticator",
                              "the window with two-factor off is closed by setting the app up again, so that is where " +
                              "the reader is put");

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser after = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)) ??
                       throw new InvalidOperationException("the account should still exist");

        (await userManager.GetAuthenticatorKeyAsync(after))
            .Should().NotBe(keyBefore, "a reset that leaves the old secret working is not a reset");

        (await userManager.GetTwoFactorEnabledAsync(after))
            .Should().BeFalse("the enabled flag is enabled against the key that was just thrown away");
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
                                 "an empty href is what an unresolvable asp-page produces, and it is indistinguishable " +
                                 "from a working button until it is clicked");
    }

    /// <summary>
    /// Turning two-factor off takes a recent proof, not just a live session - a walk-up on an unlocked
    /// browser must not be able to switch the second factor off first. The whole page is gated, so
    /// even opening it sends an unproved session to prove.
    /// </summary>
    [Fact]
    public async Task DisablingTwoFactorWithoutAProofIsRefused()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("keeps2fa@example.com", withTwoFactor: true);

        using HttpClient client = CreateClient();

        using HttpResponseMessage opened = await GetAsync(client, jar, "/Account/Manage/Disable2fa");

        // A token from a page this session may read, so the post below is refused on the proof rather
        // than on antiforgery.
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Reauthenticate");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/Disable2fa", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        opened.StatusCode.Should().Be(HttpStatusCode.Redirect, "the page itself waits for a proof");
        opened.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate");
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate", "a refused post is sent to prove, not to the page that says it worked");

        (await TwoFactorEnabledAsync(user.Id))
            .Should().BeTrue("without a proof, the second factor stays on");
    }

    /// <summary>
    /// The counterweight: the gate can be passed. A gate that refused everybody would leave the suite
    /// green while making two-factor impossible to turn off.
    /// </summary>
    [Fact]
    public async Task DisablingTwoFactorWithAProofSucceeds()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("drops2fa@example.com", withTwoFactor: true);

        using HttpClient client = CreateClient();

        await ProveAsync(client, jar);
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Manage/Disable2fa");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/Disable2fa", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.OriginalString.Should().Contain("/Account/Manage/TwoFactorAuthentication");

        (await TwoFactorEnabledAsync(user.Id)).Should().BeFalse();
    }

    /// <summary>
    /// A locked-out account cannot earn a proof - the password scheme refuses it before comparing
    /// anything - and so cannot reach the page that turns two-factor off.
    /// </summary>
    [Fact]
    public async Task ALockedOutAccountCannotProveAndSoCannotDisableTwoFactor()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("lockedout2fa@example.com", withTwoFactor: true);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser locked = (await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;
            (await userManager.SetLockoutEndDateAsync(locked, DateTimeOffset.UtcNow.AddMinutes(10))).Succeeded.Should().BeTrue();
        }

        using HttpClient client = CreateClient();

        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Reauthenticate");

        using HttpResponseMessage proof = await PostAsync(client, jar, "/Account/Reauthenticate", new()
        {
            ["Input.Password"] = SeededPassword,
            ["__RequestVerificationToken"] = token,
        });

        using HttpResponseMessage opened = await GetAsync(client, jar, "/Account/Manage/Disable2fa");

        proof.StatusCode.Should().Be(HttpStatusCode.OK, "a refused proof renders the page again rather than redirecting on");
        opened.StatusCode.Should().Be(HttpStatusCode.Redirect, "no proof was earned, so the page still waits for one");
        (await TwoFactorEnabledAsync(user.Id))
            .Should().BeTrue("a locked-out account is refused before its password is even compared");
    }

    /// <summary>
    /// <paramref name="post"/> rendered ten recovery codes that redeem for the account, and set no
    /// <c>TempData</c> cookie that could carry them to another request.
    /// </summary>
    private async Task ShouldShowLiveCodesAndCarryNoneAsync(HttpResponseMessage post, long userId)
    {
        string html = await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        post.StatusCode.Should().Be(HttpStatusCode.OK, "the codes are rendered by the post that minted them");

        List<string> codes = RecoveryCodesIn(html);
        codes.Should().HaveCount(10, "ten codes are generated, and a code that is generated but not displayed is lost");

        IEnumerable<string> tempData = post.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) ?
            cookies.Where(c => c.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal)) :
            [];

        tempData.Select(c => c.Split(';')[0].Split('=', 2)[1])
            .Should().AllSatisfy(value => value.Should().BeEmpty(
                "a TempData cookie is not bound to the account and stays decryptable once read, so the codes must " +
                "not ride in one - an expiry deleting an old one is fine"));

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)) ??
                      throw new InvalidOperationException("the account should exist");

        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded
            .Should().BeTrue("the codes shown have to be the codes stored, or showing them was worth nothing");
    }

    private async Task<bool> TwoFactorEnabledAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)) ??
                      throw new InvalidOperationException("the account should exist");

        return await userManager.GetTwoFactorEnabledAsync(user);
    }

    /// <summary>
    /// An account with its cookie already in a jar, optionally with an authenticator configured and
    /// two-factor on.
    /// </summary>
    /// <summary>
    /// The reset is what <c>Disable2fa</c>'s code requirement would otherwise be worth nothing
    /// against: both end with two-factor off, so a session alone must not be able to take either. The
    /// whole page is gated, so even opening it sends an unproved session to prove first.
    /// </summary>
    [Fact]
    public async Task ResettingTheAuthenticatorKeyIsRefusedWithoutAProof()
    {
        (HSUser user, CookieJar jar) = await SeedAsync("reset-unproved@example.com", withTwoFactor: true);

        string keyBefore = await AuthenticatorKeyAsync(user.Id);

        using HttpClient client = CreateClient();

        using HttpResponseMessage opened = await GetAsync(client, jar, "/Account/Manage/ResetAuthenticator");

        // A token from a page this session may read, so the post below is refused on the proof rather
        // than on antiforgery.
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Reauthenticate");

        using HttpResponseMessage post = await PostAsync(client, jar, "/Account/Manage/ResetAuthenticator", new()
        {
            ["__RequestVerificationToken"] = token,
        });

        opened.StatusCode.Should().Be(HttpStatusCode.Redirect, "the page itself waits for a proof");
        opened.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate");
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.OriginalString
            .Should().Contain("/Account/Reauthenticate", "a refused reset is sent to prove, not on to the next step");

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser after = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)) ??
                       throw new InvalidOperationException("the account should still exist");

        (await userManager.GetAuthenticatorKeyAsync(after))
            .Should().Be(keyBefore, "an unproved reset must not move the key");
        (await userManager.GetTwoFactorEnabledAsync(after))
            .Should().BeTrue("an unproved reset must not clear the second factor either");
    }

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
            HSUser fresh = await userManager.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)) ??
                           throw new InvalidOperationException("the account should exist");

            await userManager.ResetAuthenticatorKeyAsync(fresh);
            await userManager.SetTwoFactorEnabledAsync(fresh, true);
        }

        return (user, jar);
    }

    /// <summary>
    /// Earns <paramref name="jar"/>'s session a recent proof at <c>Account/Reauthenticate</c>, the way
    /// <see cref="EnrolmentFlowHelper.ReauthenticateAsync"/> does for a client that carries its own
    /// cookies.
    /// </summary>
    private async Task ProveAsync(HttpClient client, CookieJar jar)
    {
        string token = await GetAntiforgeryTokenAsync(client, jar, "/Account/Reauthenticate");

        using HttpResponseMessage proved = await PostAsync(client, jar, "/Account/Reauthenticate", new()
        {
            ["Input.Password"] = SeededPassword,
            ["__RequestVerificationToken"] = token,
        });

        proved.StatusCode.Should().Be(HttpStatusCode.Redirect, "the proof is setup for a test, not what it verifies");
    }

    private async Task<string> AuthenticatorKeyAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)) ??
                      throw new InvalidOperationException("the account should exist");

        return await userManager.GetAuthenticatorKeyAsync(user) ??
               throw new InvalidOperationException("the account should have an authenticator key");
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

    private static List<string> RecoveryCodesIn(string html)
    {
        const string Marker = "<code class=\"recovery-code";

        List<string> codes = [];
        int at = html.IndexOf(Marker, StringComparison.Ordinal);

        while (at >= 0)
        {
            int open = html.IndexOf('>', at) + 1;
            codes.Add(html[open..html.IndexOf('<', open)]);
            at = html.IndexOf(Marker, open, StringComparison.Ordinal);
        }

        return codes;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
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
