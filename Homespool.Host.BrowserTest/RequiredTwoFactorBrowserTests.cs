using System;
using System.Globalization;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Homespool.Host.E2ETest;
using Homespool.Model.Entities;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// <c>Security:RequireTwoFactor</c> holds an account on a page whose script still runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a browser can see this.</b> A held account's request for a script used to be answered with
/// the enrolment page, a 200 once the redirect was followed, and <c>nosniff</c> is what makes the
/// browser refuse HTML as JavaScript - so the status codes all looked right and the page was dead.
/// <c>RequiredTwoFactorTests</c> pins the response; this is the page the response is for.
/// </para>
/// <para>
/// The passkey button is the witness because it is drawn by a script: the page renders it
/// <c>hidden</c> and <c>passkey-signin.js</c> shows it, where WebAuthn exists. The proof page offers
/// it only to an account that already holds a passkey, so the account is given one.
/// </para>
/// </remarks>
public sealed class RequiredTwoFactorBrowserTests(Browsers browsers)
{
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AHeldAccountsProofPageStillOffersAPasskey(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync(
            $"browser-held-passkey-{engine}",
            factory =>
            {
                factory.ConfigurationOverrides["Security:RequireTwoFactor"] = "true";
                factory.ConfigurationOverrides["Security:PasskeyServerDomain"] = "localhost";
            });

        const string email = "held-passkey@example.com";
        (HSUser user, System.Net.Http.HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(host.Factory, email);
        client.Dispose();

        await SeedPasskeyAsync(host, user);

        await using IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);
        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/Account/Login");

        if (!await page.EvaluateAsync<bool>("() => !!window.PublicKeyCredential && !!navigator.credentials"))
        {
            Assert.Skip($"{engine} on this platform has no WebAuthn, so no page offers a passkey.");
        }

        await page.FillAsync("#Input_Login", email);
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#login-submit");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

        // No authenticator, so the account is held; the proof page is one it may reach.
        await page.GotoAsync("/Account/Reauthenticate?returnUrl=%2FAccount%2FManage%2FEnableAuthenticator");

        await Expect(page.Locator("#passkey-signin")).ToBeVisibleAsync();
    }

    private static async Task SeedPasskeyAsync(CameraHost host, HSUser user)
    {
        using IServiceScope scope = host.Factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser tracked = (await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)))!;

        UserPasskeyInfo passkey = new(
            credentialId: Guid.NewGuid().ToByteArray(),
            publicKey: [1, 2, 3],
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: null,
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: [],
            clientDataJson: [])
        {
            Name = "laptop",
        };

        (await users.AddOrUpdatePasskeyAsync(tracked, passkey)).Succeeded.Should().BeTrue("the passkey is setup for this test, not what it verifies");
    }
}
