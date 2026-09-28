using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Playwright;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// Adding a passkey that the browser refuses - <c>passkeys.js</c> - says why in the page's language,
/// never in the browser's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The authenticator is stood in for</b> by a script in place before the page's own, which makes
/// <c>navigator.credentials.create</c> reject the way a browser does. What a browser rejects with
/// carries a message of its own, in its own words; the page used to show that message.
/// </para>
/// <para>
/// <b>In Danish</b>, so that the page's wording and anything written in English cannot be mistaken for
/// each other.
/// </para>
/// </remarks>
public sealed class PasskeyAddTests(Browsers browsers)
{
    private const string BrowsersOwnWords = "The authenticator said something in English.";

    /// <summary>
    /// A device that already holds this account's passkey is told so, and a cancelled or timed-out
    /// ceremony is told that - each in the page's words, not the browser's.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium, "InvalidStateError", "already-there")]
    [InlineData(Browsers.WebKit, "InvalidStateError", "already-there")]
    [InlineData(Browsers.Chromium, "NotAllowedError", "cancelled")]
    [InlineData(Browsers.WebKit, "NotAllowedError", "cancelled")]
    public async Task ARefusalIsExplainedInThePagesLanguage(string engine, string rejection, string label)
    {
        await using CameraHost host = await CameraHost.StartAsync(
            $"browser-passkey-{label}",
            factory => factory.ConfigurationOverrides["Security:PasskeyServerDomain"] = "localhost");

        string email = $"passkey-{label}@example.com";
        (_, System.Net.Http.HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(host.Factory, email);
        client.Dispose();

        await using IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress, "da-DK");
        await context.AddInitScriptAsync($$"""
            Object.defineProperty(CredentialsContainer.prototype, "create", {
                configurable: true,
                value: function () { return Promise.reject(new DOMException("{{BrowsersOwnWords}}", "{{rejection}}")); }
            });
            """);

        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/Account/Login");
        await page.FillAsync("#Input_Login", email);
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#login-submit");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

        // Adding a passkey takes a recent proof, which a sign-in a moment ago is not.
        await page.GotoAsync("/Account/Reauthenticate?returnUrl=%2FAccount%2FManage%2FPasskeys");
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#password-form button[type=submit]");
        await page.WaitForURLAsync(url => url.Contains("/Account/Manage/Passkeys", StringComparison.Ordinal));

        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "da");

        ILocator form = page.Locator("#passkey-register-form");
        string expected = await form.GetAttributeAsync($"data-passkey-{label}") ??
                          throw new InvalidOperationException($"The form carries no {label} wording.");

        (await page.EvaluateAsync<string>("() => String(navigator.credentials.create)")).Should().Contain(
            BrowsersOwnWords, "the page must meet the stand-in, or what it says proves nothing about a refusal");

        await page.ClickAsync("#passkey-add");

        await Expect(page.Locator("#passkey-error")).ToHaveTextAsync(expected);
        await Expect(page.Locator("#passkey-error")).Not.ToContainTextAsync(BrowsersOwnWords);
    }
}
