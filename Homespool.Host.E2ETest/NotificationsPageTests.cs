using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Host.Test;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The notifications settings page, driven the way its script drives it: the subscription a browser
/// hands back, posted with the page's own form, and a test sent through the whole pipeline to a push
/// service standing in for the network.
/// </summary>
/// <remarks>
/// The script's half - permission, the service worker, <c>PushManager</c> - is a browser's and is
/// covered in the browser suite. What is pinned here is what the server does with what it is given.
/// </remarks>
public sealed partial class NotificationsPageTests : IAsyncLifetime
{
    private const string FirefoxOnMac = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.6; rv:131.0) Gecko/20100101 Firefox/131.0";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("notifications");
    private readonly FakePushService _pushService = new();
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(WebPushChannel.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _pushService)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }));

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();
        _pushService.Dispose();

        _scratch.Dispose();
    }

    [GeneratedRegex("data-application-server-key=\"([A-Za-z0-9_-]+)\"")]
    private static partial Regex ServerKeyAttribute();

    [Fact]
    public async Task ThePageHandsTheBrowserTheDeploymentsKeyAndIsInTheAccountMenu()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "reader@example.com");

        using (client)
        {
            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            string home = await client.GetStringAsync("/Account/Manage/Language", TestContext.Current.CancellationToken);

            Match key = ServerKeyAttribute().Match(page);
            key.Success.Should().BeTrue();
            key.Groups[1].Value.Should().HaveLength(87, "an uncompressed P-256 point is 65 bytes");

            home.Should().Contain("id=\"notifications-settings\"", "the page is reached from the account menu");
        }
    }

    [Fact]
    public async Task WithoutARecentProofNoBrowserIsAdded()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "hurried@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        using (client)
        {
            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

            page.Should().NotContain("id=\"notifications-enable\"", "the button is offered only to a proved session");
            page.Should().Contain("/Account/Reauthenticate", "and the way to prove is offered instead");

            using HttpResponseMessage response = await SubscribeAsync(client, browser.Endpoint, browser.P256dh, browser.Auth);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate");

            (await StoredAsync()).Should().BeEmpty("a browser added by a stolen session would go on hearing after it ended");
        }
    }

    /// <summary>
    /// The whole round: a proved session adds a browser, the list names it, and a test sent from the
    /// page arrives readable, in the account's language.
    /// </summary>
    [Fact]
    public async Task AProvedSessionAddsABrowserAndATestReachesIt()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        await SetLanguageAsync(user.Id, "da");

        using (client)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(client);

            using (HttpResponseMessage subscribed = await SubscribeAsync(client, browser.Endpoint, browser.P256dh, browser.Auth, FirefoxOnMac))
            {
                subscribed.StatusCode.Should().Be(HttpStatusCode.Redirect);
            }

            WebPushDestination stored = (await StoredAsync()).Should().ContainSingle().Subject;
            stored.UserId.Should().Be(user.Id);
            stored.Name.Should().Be("Firefox · macOS");

            string listed = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            listed.Should().Contain("Firefox · macOS");
            listed.Should().NotContain(browser.Endpoint, "an endpoint is a capability URL and is never rendered");

            using (HttpResponseMessage tested = await PostHandlerAsync(client, "Test", stored.Uuid))
            {
                tested.StatusCode.Should().Be(HttpStatusCode.Redirect);
            }

            FakePush push = _pushService.Received.Should().ContainSingle().Subject;
            JsonElement payload = browser.DecryptJson(push.Body);

            payload.GetProperty("title").GetString().Should().Be("Homespool-test", "composed in the account's stored language");
            payload.GetProperty("url").GetString().Should().Be("/Account/Manage/Notifications");

            string after = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            after.Should().Contain("En testnotifikation er på vej.", "the page says the test went");
        }
    }

    [Fact]
    public async Task AnEndpointOffTheListIsRefusedWithASentence()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "prober@example.com");
        using FakePushBrowser browser = new("https://homespool:8080/internal");

        using (client)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(client);

            using HttpResponseMessage response = await SubscribeAsync(client, browser.Endpoint, browser.P256dh, browser.Auth);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await StoredAsync()).Should().BeEmpty();

            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            page.Should().Contain("push service isn’t one Homespool sends to");
        }
    }

    [Fact]
    public async Task AnotherAccountsBrowserCannotBeRemovedOrTested()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner2@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "stranger@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        using (ownerClient)
        using (stranger)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(ownerClient);

            using (await SubscribeAsync(ownerClient, browser.Endpoint, browser.P256dh, browser.Auth))
            {
            }

            Guid uuid = (await StoredAsync()).Single().Uuid;

            using (await PostHandlerAsync(stranger, "Test", uuid))
            using (await PostHandlerAsync(stranger, "Remove", uuid))
            {
            }

            _pushService.Received.Should().BeEmpty();
            (await StoredAsync()).Should().ContainSingle().Which.UserId.Should().Be(owner.Id);

            string page = await stranger.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            page.Should().Contain("That browser is not on your list.");
        }
    }

    /// <summary>
    /// Each way a test can fail is said on the page in its own words, because each asks something
    /// different of the reader: nothing, try again, or look at the browser.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Gone, "That browser no longer accepts notifications, so it has been removed.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The browser’s push service could not be reached. Try again in a moment.")]
    [InlineData(HttpStatusCode.BadRequest, "The browser’s push service refused the notification.")]
    public async Task EachFailedTestIsSaidOnThePage(HttpStatusCode answer, string sentence)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "tester@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        Guid uuid = await AddBrowserAsync(user.Id, browser);

        _pushService.Answer = answer;

        using (client)
        {
            using (HttpResponseMessage tested = await PostHandlerAsync(client, "Test", uuid))
            {
                tested.StatusCode.Should().Be(HttpStatusCode.Redirect);
            }

            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            page.Should().Contain(sentence);
        }

        (await StoredAsync()).Should().HaveCount(answer == HttpStatusCode.Gone ? 0 : 1, "only a browser its service calls gone is removed");
    }

    [Fact]
    public async Task ASecondTestStraightAfterTheFirstIsHeldBack()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "eager@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        Guid uuid = await AddBrowserAsync(user.Id, browser);

        using (client)
        {
            using (await PostHandlerAsync(client, "Test", uuid))
            using (await PostHandlerAsync(client, "Test", uuid))
            {
            }

            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            page.Should().Contain("A test was just sent to that browser. Wait a few seconds before sending another.");
        }

        _pushService.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task ASubscriptionWithUnusableKeysIsRefusedWithASentence()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "garbled@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();

        using (client)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(client);

            using (await SubscribeAsync(client, browser.Endpoint, "not-a-key", browser.Auth))
            {
            }

            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            page.Should().Contain("The browser’s subscription could not be used. Try enabling notifications again.");
        }

        (await StoredAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ABrowserBeyondTheLimitIsRefusedWithASentence()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "collector@example.com");
        List<FakePushBrowser> browsers = [.. Enumerable.Range(0, NotificationDestinationService.MaxPerAccount + 1).Select(_ => FakePushService.NewBrowser())];

        try
        {
            foreach (FakePushBrowser browser in browsers.Take(NotificationDestinationService.MaxPerAccount))
            {
                await AddBrowserAsync(user.Id, browser);
            }

            using (client)
            {
                await EnrolmentFlowHelper.ReauthenticateAsync(client);

                FakePushBrowser oneTooMany = browsers[^1];

                using (await SubscribeAsync(client, oneTooMany.Endpoint, oneTooMany.P256dh, oneTooMany.Auth))
                {
                }

                string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
                page.Should().Contain($"You already have {NotificationDestinationService.MaxPerAccount} browsers receiving notifications.");
            }

            (await StoredAsync()).Should().HaveCount(NotificationDestinationService.MaxPerAccount);
        }
        finally
        {
            browsers.ForEach(browser => browser.Dispose());
        }
    }

    [Fact]
    public async Task ARemovedBrowserHearsNothingMore()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "leaver@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        Guid uuid = await AddBrowserAsync(user.Id, browser);

        using (client)
        {
            using (await PostHandlerAsync(client, "Remove", uuid))
            {
            }

            string removed = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            removed.Should().Contain("The browser no longer receives notifications.");

            using (await PostHandlerAsync(client, "Test", uuid))
            {
            }

            string tested = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            tested.Should().Contain("That browser is not on your list.", "a test after the removal finds nothing to send to");
        }

        (await StoredAsync()).Should().BeEmpty();
        _pushService.Received.Should().BeEmpty();
    }

    /// <summary>
    /// Everything is on to start with; what is left unticked when saving is turned off, and the page
    /// shows it so afterwards.
    /// </summary>
    [Fact]
    public async Task UntickedKindsAreTurnedOff()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "chooser@example.com");

        using (client)
        {
            string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

            page.Should().Contain("id=\"kind-QueueHeld\"").And.Contain("checked=\"checked\"", "a new account hears everything");

            using FormUrlEncodedContent form = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
                new("enabled", nameof(NotificationKind.PrinterNeedsAttention)),
                new("enabled", nameof(NotificationKind.PrintFinished)),
            ]);

            using HttpResponseMessage saved = await client.PostAsync("/Account/Manage/Notifications?handler=Kinds", form,
                                                                     TestContext.Current.CancellationToken);

            saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                string? muted = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                           .Users.Where(row => row.Id == user.Id)
                                           .Select(row => row.MutedNotifications)
                                           .SingleAsync(TestContext.Current.CancellationToken);

                muted.Should().Be("PrintDidNotFinish QueueHeld FilamentChangeSoon PrinterLost");
            }

            string after = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

            Regex.IsMatch(after, "id=\"kind-QueueHeld\"[^>]*checked").Should().BeFalse("what was turned off shows as off");
            Regex.IsMatch(after, "id=\"kind-PrintFinished\"[^>]*checked").Should().BeTrue();
        }
    }

    /// <summary>
    /// The health switch is an administrator's alone: offered to them, and saved off when unticked. An
    /// ordinary account neither sees it nor stores it - the test above pins what its save writes.
    /// </summary>
    [Fact]
    public async Task OnlyAnAdministratorIsOfferedTheHealthSwitch()
    {
        (HSUser _, HttpClient member) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "member@example.com");
        (HSUser admin, HttpClient administrator) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        using (member)
        using (administrator)
        {
            string memberPage = await member.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
            string adminPage = await administrator.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

            memberPage.Should().NotContain("id=\"kind-ServiceHealth\"");
            Regex.IsMatch(adminPage, "id=\"kind-ServiceHealth\"[^>]*checked").Should().BeTrue("an administrator hears it until they turn it off");

            using FormUrlEncodedContent form = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(adminPage)),
                .. NotificationMutes.Choosable.Select(kind => new KeyValuePair<string, string>("enabled", kind.ToString())),
            ]);

            using HttpResponseMessage saved = await administrator.PostAsync("/Account/Manage/Notifications?handler=Kinds", form,
                                                                            TestContext.Current.CancellationToken);

            saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

            using IServiceScope scope = _factory.Services.CreateScope();
            string? muted = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                       .Users.Where(row => row.Id == admin.Id)
                                       .Select(row => row.MutedNotifications)
                                       .SingleAsync(TestContext.Current.CancellationToken);

            muted.Should().Be("ServiceHealth");
        }
    }

    /// <summary>
    /// The printers an account may see are listed, all ticked; an unticked one is muted, by its public
    /// id.
    /// </summary>
    [Fact]
    public async Task AnUntickedPrinterIsMuted()
    {
        (_, _, int printerId, long userId) = await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);
        HSUser user = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);
        Guid uuid;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            uuid = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                              .Printers.Where(printer => printer.Id == printerId)
                              .Select(printer => printer.Uuid)
                              .SingleAsync(TestContext.Current.CancellationToken);
        }

        using HttpClient client = await EnrolmentFlowHelper.SignInAsAsync(_factory, user);

        string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
        Regex.IsMatch(page, $"id=\"printer-{uuid}\"[^>]*checked").Should().BeTrue("a printer is heard about until muted");

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        ]);

        using HttpResponseMessage saved = await client.PostAsync("/Account/Manage/Notifications?handler=Printers", form,
                                                                 TestContext.Current.CancellationToken);

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            string? muted = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                       .Users.Where(row => row.Id == userId)
                                       .Select(row => row.MutedPrinters)
                                       .SingleAsync(TestContext.Current.CancellationToken);

            muted.Should().Be(uuid.ToString());
        }

        string after = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);
        Regex.IsMatch(after, $"id=\"printer-{uuid}\"[^>]*checked").Should().BeFalse();
    }

    /// <summary>
    /// The two files a browser fetches on its own rather than through a page, with the types it
    /// insists on: a service worker served as anything but JavaScript is refused outright.
    /// </summary>
    [Fact]
    public async Task TheWorkerAndTheManifestAreServedAsWhatTheyAre()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage worker = await client.GetAsync("/js/push-worker.js", TestContext.Current.CancellationToken);
        using HttpResponseMessage manifest = await client.GetAsync("/site.webmanifest", TestContext.Current.CancellationToken);

        worker.StatusCode.Should().Be(HttpStatusCode.OK);
        worker.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");

        manifest.StatusCode.Should().Be(HttpStatusCode.OK);
        manifest.Content.Headers.ContentType!.MediaType.Should().Be("application/manifest+json");
    }

    /// <summary>
    /// Every icon the manifest names exists and is the size it claims. A browser that cannot fetch
    /// one says nothing; it just installs Homespool with a blank or blurred icon.
    /// </summary>
    [Fact]
    public async Task EveryIconTheManifestNamesIsServedAtItsStatedSize()
    {
        using HttpClient client = _factory.CreateClient();

        using JsonDocument manifest = JsonDocument.Parse(
            await client.GetStringAsync("/site.webmanifest", TestContext.Current.CancellationToken));

        JsonElement[] icons = [.. manifest.RootElement.GetProperty("icons").EnumerateArray()];

        icons.Should().Contain(icon => icon.GetProperty("purpose").GetString() == "maskable",
                               "Android crops an icon without a maskable version to a circle inside a white one");

        foreach (JsonElement icon in icons)
        {
            string source = icon.GetProperty("src").GetString()!;

            using HttpResponseMessage response = await client.GetAsync(source, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK, source);
            response.Content.Headers.ContentType!.MediaType.Should().Be(icon.GetProperty("type").GetString(), source);

            string sizes = icon.GetProperty("sizes").GetString()!;

            if (sizes != "any")
            {
                // A PNG's IHDR: width and height, big-endian, at bytes 16 and 20.
                byte[] png = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
                int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

                $"{width}x{height}".Should().Be(sizes, source);
            }
        }
    }

    private async Task<List<WebPushDestination>> StoredAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .WebPushDestinations.AsNoTracking()
                          .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Stores <paramref name="browser"/> as <paramref name="userId"/>'s, as a subscription would have.</summary>
    private async Task<Guid> AddBrowserAsync(long userId, FakePushBrowser browser)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext db = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        WebPushDestination destination = new()
        {
            UserId = userId,
            Endpoint = browser.Endpoint,
            P256dh = browser.P256dh,
            Auth = browser.Auth,
            Name = "Test browser",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.WebPushDestinations.Add(destination);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return destination.Uuid;
    }

    private async Task SetLanguageAsync(long userId, string language)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                   .Users.Where(user => user.Id == userId)
                   .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Language, language),
                                       TestContext.Current.CancellationToken);
    }

    /// <summary>The subscribe form as the script submits it, from a browser naming itself.</summary>
    private static async Task<HttpResponseMessage> SubscribeAsync(HttpClient client,
                                                                  string endpoint,
                                                                  string p256dh,
                                                                  string auth,
                                                                  string? userAgent = null)
    {
        string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("endpoint", endpoint),
            new("p256dh", p256dh),
            new("auth", auth),
        ]);

        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Manage/Notifications?handler=Subscribe") { Content = form };

        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostHandlerAsync(HttpClient client, string handler, Guid uuid)
    {
        string page = await client.GetStringAsync("/Account/Manage/Notifications", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
        ]);

        return await client.PostAsync($"/Account/Manage/Notifications?handler={handler}", form, TestContext.Current.CancellationToken);
    }
}
