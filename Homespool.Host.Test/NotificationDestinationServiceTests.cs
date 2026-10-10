using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Notifications;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A person's own destinations: what may be stored, who a browser belongs to, and what a test send
/// records on it.
/// </summary>
public sealed class NotificationDestinationServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = WebPushRig.NewDatabasePath();
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private WebPushRig _rig = null!;

    public async ValueTask InitializeAsync()
    {
        _rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider(), time: _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        WebPushRig.Delete(_databasePath);
    }

    private Task<T> WithServiceAsync<T>(Func<NotificationDestinationService, Task<T>> action)
    {
        return _rig.InScopeAsync(services => action(services.GetRequiredService<NotificationDestinationService>()));
    }

    private Task<List<WebPushDestination>> StoredAsync()
    {
        return _rig.InScopeAsync(services =>
            services.GetRequiredService<HomespoolDbContext>().WebPushDestinations.AsNoTracking()
                    .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A test send after the cooldown has passed, as a person pressing the button again later would.</summary>
    private Task<TestSendResult> TestLaterAsync(long userId, Guid uuid)
    {
        _clock.Advance(NotificationDestinationService.TestCooldown);

        return WithServiceAsync(service => service.SendTestAsync(userId, uuid, TestContext.Current.CancellationToken));
    }

    private Task<WebPushSubscribeResult> SubscribeAsync(long userId, string endpoint, string p256dh, string auth)
    {
        return WithServiceAsync(service => service.SubscribeWebPushAsync(
                                    userId, endpoint, p256dh, auth, "Firefox · macOS", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABrowserIsStoredForTheAccountThatSubscribedIt()
    {
        HSUser user = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        WebPushSubscribeResult result = await SubscribeAsync(user.Id, browser.Endpoint, browser.P256dh, browser.Auth);

        result.Should().Be(WebPushSubscribeResult.Subscribed);

        WebPushDestination stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.UserId.Should().Be(user.Id);
        stored.Endpoint.Should().Be(browser.Endpoint);
        stored.Name.Should().Be("Firefox · macOS");
        stored.Kind.Should().Be(Model.NotificationChannelKind.WebPush);
    }

    [Fact]
    public async Task AnEndpointOffTheListIsNotStored()
    {
        HSUser user = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = new("https://10.0.0.5/internal");

        WebPushSubscribeResult result = await SubscribeAsync(user.Id, browser.Endpoint, browser.P256dh, browser.Auth);

        result.Should().Be(WebPushSubscribeResult.EndpointNotAllowed);
        (await StoredAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task KeysThatCannotBeEncryptedToAreNotStored()
    {
        HSUser user = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        WebPushSubscribeResult result = await SubscribeAsync(user.Id, browser.Endpoint, "AAAA", browser.Auth);

        result.Should().Be(WebPushSubscribeResult.KeysInvalid);
        (await StoredAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// An endpoint can be learned without the browser, so another account posting it - with keys of its
    /// own choosing, or even the owner's - takes nothing, and the owner goes on hearing through it.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsEndpointIsRefusedAndTheOwnerKeepsIt()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        HSUser stranger = await _rig.AddUserAsync("stranger@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        using FakePushBrowser impostor = new(browser.Endpoint);

        await SubscribeAsync(owner.Id, browser.Endpoint, browser.P256dh, browser.Auth);

        (await SubscribeAsync(stranger.Id, impostor.Endpoint, impostor.P256dh, impostor.Auth)).Should().Be(WebPushSubscribeResult.EndpointTaken);
        (await SubscribeAsync(stranger.Id, browser.Endpoint, browser.P256dh, browser.Auth)).Should().Be(WebPushSubscribeResult.EndpointTaken);

        WebPushDestination stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.UserId.Should().Be(owner.Id);
        stored.P256dh.Should().Be(browser.P256dh);
        stored.Auth.Should().Be(browser.Auth);

        TestSendResult outcome = await WithServiceAsync(service => service.SendTestAsync(owner.Id, stored.Uuid, TestContext.Current.CancellationToken));

        outcome.Should().Be(TestSendResult.Delivered);
        browser.DecryptJson(_rig.PushService.Received.Should().ContainSingle().Subject.Body)
               .GetProperty("url").GetString().Should().Be(NotificationDestinationService.SettingsPath);
    }

    /// <summary>
    /// The owner's own browser posting again with new keys is the one case an existing endpoint is
    /// updated.
    /// </summary>
    [Fact]
    public async Task TheOwnersBrowserRefreshingItsKeysReplacesThem()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser before = FakePushService.NewBrowser();
        using FakePushBrowser after = new(before.Endpoint);

        await SubscribeAsync(owner.Id, before.Endpoint, before.P256dh, before.Auth);
        (await SubscribeAsync(owner.Id, after.Endpoint, after.P256dh, after.Auth)).Should().Be(WebPushSubscribeResult.Subscribed);

        WebPushDestination stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.UserId.Should().Be(owner.Id);
        stored.P256dh.Should().Be(after.P256dh);
        stored.Auth.Should().Be(after.Auth);
    }

    [Fact]
    public async Task AnotherAccountsBrowserCannotBeRemovedOrTested()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        HSUser stranger = await _rig.AddUserAsync("stranger@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        bool removed = await WithServiceAsync(service => service.RemoveAsync(stranger.Id, destination.Uuid, TestContext.Current.CancellationToken));
        TestSendResult tested = await WithServiceAsync(service => service.SendTestAsync(stranger.Id, destination.Uuid, TestContext.Current.CancellationToken));

        removed.Should().BeFalse();
        tested.Should().Be(TestSendResult.NotFound);
        (await StoredAsync()).Should().ContainSingle();
        _rig.PushService.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task TheOwnerCanRemoveTheirBrowser()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        bool removed = await WithServiceAsync(service => service.RemoveAsync(owner.Id, destination.Uuid, TestContext.Current.CancellationToken));

        removed.Should().BeTrue();
        (await StoredAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Composed in the account's stored language, as every notification will be, whatever language the
    /// request that asked for it was in.
    /// </summary>
    [Fact]
    public async Task ATestArrivesInTheOwnersLanguage()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com", language: "da");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        TestSendResult outcome = await WithServiceAsync(service => service.SendTestAsync(owner.Id, destination.Uuid, TestContext.Current.CancellationToken));

        outcome.Should().Be(TestSendResult.Delivered);

        System.Text.Json.JsonElement payload = browser.DecryptJson(_rig.PushService.Received.Should().ContainSingle().Subject.Body);
        payload.GetProperty("title").GetString().Should().Be("Homespool-test");
        payload.GetProperty("url").GetString().Should().Be(NotificationDestinationService.SettingsPath);

        WebPushDestination stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.LastDeliveredAt.Should().NotBeNull();
        stored.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task FailuresAreCountedUntilADeliverySucceeds()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        _rig.PushService.Answer = HttpStatusCode.ServiceUnavailable;
        await TestLaterAsync(owner.Id, destination.Uuid);
        await TestLaterAsync(owner.Id, destination.Uuid);

        WebPushDestination failing = (await StoredAsync()).Should().ContainSingle().Subject;
        failing.ConsecutiveFailures.Should().Be(2);
        failing.LastFailedAt.Should().NotBeNull();
        failing.LastDeliveredAt.Should().BeNull();

        _rig.PushService.Answer = HttpStatusCode.Created;
        await TestLaterAsync(owner.Id, destination.Uuid);

        WebPushDestination recovered = (await StoredAsync()).Should().ContainSingle().Subject;
        recovered.ConsecutiveFailures.Should().Be(0);
        recovered.LastDeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ABrowserThePushServiceSaysIsGoneIsRemoved()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        _rig.PushService.Answer = HttpStatusCode.Gone;

        TestSendResult outcome = await WithServiceAsync(service => service.SendTestAsync(owner.Id, destination.Uuid, TestContext.Current.CancellationToken));

        outcome.Should().Be(TestSendResult.Gone);
        (await StoredAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// A destination refused time after time is one that will never be accepted - and only refusals
    /// count, so a deployment that lost its internet connection keeps everybody's browsers.
    /// </summary>
    [Fact]
    public async Task RepeatedRefusalsRemoveADestinationAndOutagesDoNot()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser refused = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, refused);

        _rig.PushService.Answer = HttpStatusCode.ServiceUnavailable;

        for (int attempt = 0; attempt < NotificationDestinationService.RemoveAfterRefusals * 2; attempt++)
        {
            await TestLaterAsync(owner.Id, destination.Uuid);
        }

        (await StoredAsync()).Should().ContainSingle("an outage removes nothing, however long");

        _rig.PushService.Answer = HttpStatusCode.Forbidden;

        for (int attempt = 1; attempt < NotificationDestinationService.RemoveAfterRefusals; attempt++)
        {
            await TestLaterAsync(owner.Id, destination.Uuid);
        }

        (await StoredAsync()).Should().ContainSingle("one refusal short of the limit");

        await TestLaterAsync(owner.Id, destination.Uuid);

        (await StoredAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ATestTooSoonAfterTheLastSendIsNotSent()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        WebPushDestination destination = await _rig.AddBrowserAsync(owner.Id, browser);

        TestSendResult first = await WithServiceAsync(service => service.SendTestAsync(owner.Id, destination.Uuid, TestContext.Current.CancellationToken));
        TestSendResult second = await WithServiceAsync(service => service.SendTestAsync(owner.Id, destination.Uuid, TestContext.Current.CancellationToken));
        TestSendResult later = await TestLaterAsync(owner.Id, destination.Uuid);

        first.Should().Be(TestSendResult.Delivered);
        second.Should().Be(TestSendResult.TooSoon);
        later.Should().Be(TestSendResult.Delivered);
        _rig.PushService.Received.Should().HaveCount(2, "the refused press never reached the push service");
    }

    [Fact]
    public async Task AnAccountCannotHaveMoreThanItsShareOfDestinations()
    {
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        List<FakePushBrowser> browsers = [.. Enumerable.Range(0, NotificationDestinationService.MaxPerAccount + 1).Select(_ => FakePushService.NewBrowser())];

        try
        {
            foreach (FakePushBrowser browser in browsers.Take(NotificationDestinationService.MaxPerAccount))
            {
                (await SubscribeAsync(owner.Id, browser.Endpoint, browser.P256dh, browser.Auth)).Should().Be(WebPushSubscribeResult.Subscribed);
            }

            FakePushBrowser extra = browsers[^1];

            (await SubscribeAsync(owner.Id, extra.Endpoint, extra.P256dh, extra.Auth)).Should().Be(WebPushSubscribeResult.TooMany);
            (await SubscribeAsync(owner.Id, browsers[0].Endpoint, browsers[0].P256dh, browsers[0].Auth))
                .Should().Be(WebPushSubscribeResult.Subscribed, "a browser already on the list refreshing its keys is not a new one");
            (await StoredAsync()).Should().HaveCount(NotificationDestinationService.MaxPerAccount);
        }
        finally
        {
            browsers.ForEach(browser => browser.Dispose());
        }
    }

    /// <summary>Browser names as real user agents give them, the Chromium family first among them.</summary>
    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.6; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox · macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36", "Chrome · Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.2792.79", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari · iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S921B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36", "Samsung Internet · Android")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 OPR/114.0.0.0", "Opera · Linux")]
    [InlineData("curl/8.7.1", "Unknown browser")]
    [InlineData("", "Unknown browser")]
    public void ABrowserIsNamedFromItsUserAgent(string userAgent, string expected)
    {
        BrowserNames.FromUserAgent(userAgent, "Unknown browser").Should().Be(expected);
    }
}
