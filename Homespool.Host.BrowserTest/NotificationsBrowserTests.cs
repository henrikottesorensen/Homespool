using System;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Homespool.Data;
using Homespool.Host.E2ETest;
using Homespool.Model.Entities;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// Enabling notifications on a browser - <c>push-notifications.js</c> and its service worker - from the
/// button to the stored subscription and back to the page recognising its own row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The push service is stood in for</b>, because a headless browser has none to subscribe with.
/// The stand-in makes a real P-256 key pair and secret, so what the page posts is a subscription the
/// server's checks accept - an obviously fake key would be refused at the door and prove nothing. It
/// remembers the subscription for the tab's session, as a browser would, so the page after the redirect
/// finds it.
/// </para>
/// <para>
/// <b>The permission is stood in for as well.</b> Headless Chromium answers "denied" whatever
/// Playwright grants, so the page would stop at its "blocked" sentence - which is the one path this does
/// not need a browser to prove.
/// </para>
/// </remarks>
public sealed class NotificationsBrowserTests(Browsers browsers)
{
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/browser-test";

    private const string StandInPushService = """
        (function () {
            const saved = "homespool-test-subscription";

            function toBase64Url(buffer) {
                return btoa(String.fromCharCode(...new Uint8Array(buffer)))
                    .replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
            }

            function fromBase64Url(text) {
                const base64 = text.replace(/-/g, "+").replace(/_/g, "/");
                return Uint8Array.from(atob(base64 + "===".slice((base64.length + 3) % 4)), c => c.charCodeAt(0));
            }

            function subscription(stored) {
                return {
                    endpoint: stored.endpoint,
                    options: { applicationServerKey: fromBase64Url(stored.serverKey).buffer },
                    toJSON() { return { endpoint: stored.endpoint, keys: { p256dh: stored.p256dh, auth: stored.auth } }; },
                    unsubscribe() { sessionStorage.removeItem(saved); return Promise.resolve(true); },
                };
            }

            Object.defineProperty(Notification, "permission", { configurable: true, get: () => "granted" });
            Notification.requestPermission = () => Promise.resolve("granted");

            PushManager.prototype.getSubscription = function () {
                const stored = sessionStorage.getItem(saved);
                return Promise.resolve(stored ? subscription(JSON.parse(stored)) : null);
            };

            PushManager.prototype.subscribe = async function (options) {
                const pair = await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
                const stored = {
                    endpoint: "ENDPOINT",
                    serverKey: toBase64Url(new Uint8Array(options.applicationServerKey)),
                    p256dh: toBase64Url(await crypto.subtle.exportKey("raw", pair.publicKey)),
                    auth: toBase64Url(crypto.getRandomValues(new Uint8Array(16))),
                };
                sessionStorage.setItem(saved, JSON.stringify(stored));
                return subscription(stored);
            };
        })();
        """;

    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task EnablingSubscribesThisBrowserAndThePageKnowsItAfterwards(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync($"browser-notifications-{engine}");

        string email = $"notified-{engine}@example.com";
        (HSUser user, System.Net.Http.HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(host.Factory, email);
        client.Dispose();

        await using IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);
        await context.AddInitScriptAsync(StandInPushService.Replace("ENDPOINT", Endpoint, StringComparison.Ordinal));

        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/Account/Login");
        await page.FillAsync("#Input_Login", email);
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#login-submit");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

        // Adding a browser takes a recent proof, which a sign-in a moment ago is not.
        await page.GotoAsync("/Account/Reauthenticate?returnUrl=%2FAccount%2FManage%2FNotifications");
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#password-form button[type=submit]");
        await page.WaitForURLAsync(url => url.Contains("/Account/Manage/Notifications", StringComparison.Ordinal));

        ILocator enable = page.Locator("#notifications-enable");
        await Expect(enable).ToBeVisibleAsync();

        await enable.ClickAsync();

        await page.WaitForURLAsync(url => url.Contains("/Account/Manage/Notifications", StringComparison.Ordinal));
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Notifications are enabled on this browser.");

        // After the redirect: the worker is registered, the subscription is found again, and the page
        // matches it to its row by the endpoint's hash rather than by anything it was told.
        await Expect(page.Locator("[data-this-browser]")).ToBeVisibleAsync();
        await Expect(page.Locator("#notifications-state")).ToHaveTextAsync("This browser receives notifications.");
        await Expect(enable).ToBeHiddenAsync();

        (await page.EvaluateAsync<int>("async () => (await navigator.serviceWorker.getRegistrations()).length"))
            .Should().Be(1, "the worker the notifications arrive through is registered");

        using IServiceScope scope = host.Factory.Services.CreateScope();

        WebPushDestination stored = (await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                                .WebPushDestinations.AsNoTracking()
                                                .ToListAsync(TestContext.Current.CancellationToken))
                                    .Should().ContainSingle().Subject;

        stored.UserId.Should().Be(user.Id);
        stored.Endpoint.Should().Be(Endpoint);
        stored.Name.Should().StartWith(engine == Browsers.Chromium ? "Chrome" : "Safari", "the name is read from the browser that subscribed");
    }
}
