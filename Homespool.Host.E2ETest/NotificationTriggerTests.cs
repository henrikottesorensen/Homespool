using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
using Homespool.Host.Telemetry;
using Homespool.Host.Test;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Printer happenings reaching the right people's browsers, through the running watcher, dispatcher
/// and router, to a push service standing in for the network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the committed rows</b>, as the queue leaves them, and through the attention watch
/// as the telemetry writer feeds it - so what is tested is the path a real print takes after the
/// queue has done its work. The writer's own feeding is tested with a fake printer in
/// <c>FakePrinterIntegrationTests</c>.
/// </para>
/// <para>
/// <b>One team, seven people, each with a browser</b>: the owner (who queues, and reads Danish), a
/// viewer who may see the printer but not its queue, somebody who has muted two kinds, somebody who has
/// muted this printer, a closed account, a member whose membership grants nothing, and an outsider on
/// another team. Each test says who should hear, and every other browser
/// is checked to have heard nothing.
/// </para>
/// </remarks>
public sealed class NotificationTriggerTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("notification-triggers");
    private readonly FakePushService _pushService = new();
    private readonly Dictionary<string, FakePushBrowser> _browsers = [];
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    private Printer _printer = null!;
    private long _owner;
    private long _viewer;
    private long _muted;
    private long _closed;
    private long _outsider;
    private long _bystander;
    private long _printerMuted;
    private long _fileId;

    public async ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(WebPushChannel.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _pushService)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }));

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        _owner = await PersonAsync("owner");
        _viewer = await PersonAsync("viewer");
        _muted = await PersonAsync("muted");
        _closed = await PersonAsync("closed");
        _outsider = await PersonAsync("outsider");
        _bystander = await PersonAsync("bystander");
        _printerMuted = await PersonAsync("printer-muted");

        await WithDbAsync(async db =>
        {
            Team workshop = new() { Name = "Workshop" };
            Team elsewhere = new() { Name = "Elsewhere" };
            db.Teams.AddRange(workshop, elsewhere);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            _printer = new Printer { Uuid = Guid.NewGuid(), TeamId = workshop.Id, Name = "Core One+" };
            db.Printers.Add(_printer);

            string everything = CapabilitySet.Format([Capability.ViewPrinter, Capability.ViewQueue, Capability.Print]);

            db.TeamMembers.AddRange(
                new TeamMember { TeamId = workshop.Id, UserId = _owner, Capabilities = everything },
                new TeamMember { TeamId = workshop.Id, UserId = _viewer, Capabilities = CapabilitySet.Format([Capability.ViewPrinter]) },
                new TeamMember { TeamId = workshop.Id, UserId = _muted, Capabilities = everything },
                new TeamMember { TeamId = workshop.Id, UserId = _closed, Capabilities = everything },
                new TeamMember { TeamId = elsewhere.Id, UserId = _outsider, Capabilities = everything },
                new TeamMember { TeamId = workshop.Id, UserId = _bystander, Capabilities = string.Empty },
                new TeamMember { TeamId = workshop.Id, UserId = _printerMuted, Capabilities = everything });

            PrintFile file = new() { UserId = _owner, Name = "benchy.bgcode", Size = 1024 };
            db.PrintFiles.Add(file);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            _fileId = file.Id;

            await db.Users.Where(user => user.Id == _owner)
                    .ExecuteUpdateAsync(set => set.SetProperty(user => user.Language, "da"), TestContext.Current.CancellationToken);
            await db.Users.Where(user => user.Id == _muted)
                    .ExecuteUpdateAsync(set => set.SetProperty(user => user.MutedNotifications, "PrinterNeedsAttention QueueHeld"),
                                        TestContext.Current.CancellationToken);
            await db.Users.Where(user => user.Id == _printerMuted)
                    .ExecuteUpdateAsync(set => set.SetProperty(user => user.MutedPrinters, _printer.Uuid.ToString()),
                                        TestContext.Current.CancellationToken);
            await db.Users.Where(user => user.Id == _closed)
                    .ExecuteUpdateAsync(set => set.SetProperty(user => user.DeactivatedAt, DateTimeOffset.UtcNow),
                                        TestContext.Current.CancellationToken);
        });

        // The watcher's first look only learns what is already held; taken here, so every hold a test
        // makes afterwards is news.
        await _factory.Services.GetRequiredService<NotificationWatcher>().LookAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();
        _pushService.Dispose();

        foreach (FakePushBrowser browser in _browsers.Values)
        {
            browser.Dispose();
        }

        _scratch.Dispose();
    }

    /// <summary>
    /// A printer stopping for somebody reaches everybody who may see it and has not turned it off - in
    /// their own language, loud, and pointing at the printer.
    /// </summary>
    [Fact]
    public async Task APrinterWaitingForSomebodyReachesEverybodyWhoMaySeeIt()
    {
        AttentionWatch watch = _factory.Services.GetRequiredService<AttentionWatch>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // What the telemetry writer tells the watch: the printer was printing and is now waiting on a
        // filament runout (23829 is an MK3.5's).
        watch.Observed(_printer.Id,
                       new LiveStateSnapshot(PrinterStatus.Printing, null, null, 42),
                       new LiveStateSnapshot(PrinterStatus.Attention, 23829, null, 42),
                       now - AttentionWatch.Settle);

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 2);

        heard.Keys.Should().BeEquivalentTo(["owner", "viewer"],
                                           "the muted, the printer's muter, the closed, the member who may see nothing and the outsider hear nothing");

        heard["owner"].GetProperty("title").GetString().Should().Be("Core One+ har brug for dig");
        heard["owner"].GetProperty("body").GetString().Should().Be(PrinterErrorText.For(23829, "da"));
        heard["viewer"].GetProperty("title").GetString().Should().Be("Core One+ needs you");
        heard["viewer"].GetProperty("url").GetString().Should().Be($"/Printers/Detail/{_printer.Uuid}");

        FakePush push = _pushService.Received[0];
        push.Header("Urgency").Should().Be("high");
        heard["owner"].GetProperty("tag").GetString().Should().Be($"printer-{_printer.Id}", "the next notification about this printer replaces it");
    }

    /// <summary>
    /// A printer taking turns between two dialogs is a new wait each time by the watch's rules; the
    /// dispatcher's floor is what keeps it to one notification.
    /// </summary>
    [Fact]
    public async Task APrinterAlternatingDialogsNotifiesOnce()
    {
        AttentionWatch watch = _factory.Services.GetRequiredService<AttentionWatch>();
        NotificationWatcher watcher = _factory.Services.GetRequiredService<NotificationWatcher>();
        DateTimeOffset settled = DateTimeOffset.UtcNow - AttentionWatch.Settle;

        LiveStateSnapshot printing = new(PrinterStatus.Printing, null, null, 42);
        LiveStateSnapshot runout = new(PrinterStatus.Attention, 23829, null, 42);
        LiveStateSnapshot other = new(PrinterStatus.Attention, 23830, null, 43);

        for (int turn = 0; turn < 4; turn++)
        {
            watch.Observed(_printer.Id, printing, turn % 2 == 0 ? runout : other, settled);
            await watcher.LookAsync(TestContext.Current.CancellationToken);
            watch.Observed(_printer.Id, turn % 2 == 0 ? runout : other, printing, settled);
        }

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 2);

        heard.Keys.Should().BeEquivalentTo(["owner", "viewer"]);
        _pushService.Received.Should().HaveCount(2, "one notification each, however often the printer changes its mind");
    }

    [Fact]
    public async Task AFlappingHoldNotifiesOnce()
    {
        NotificationWatcher watcher = _factory.Services.GetRequiredService<NotificationWatcher>();

        await WithDbAsync(async db =>
        {
            db.QueuedPrints.Add(new QueuedPrint
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printer.Id,
                PrintFileId = _fileId,
                Position = 1,
                QueuedByUserId = _owner,
                QueuedByScope = CapabilitySet.Format([Capability.Print]),
            });

            db.PrintFilesOnPrinters.Add(new PrintFileOnPrinter { PrinterId = _printer.Id, PrintFileId = _fileId });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        for (int turn = 0; turn < 4; turn++)
        {
            PrintHoldReason? reason = turn % 2 == 0 ? PrintHoldReason.FileExistsUnknownSize : null;

            await WithDbAsync(db => db.PrintFilesOnPrinters
                                      .Where(row => row.PrinterId == _printer.Id)
                                      .ExecuteUpdateAsync(set => set.SetProperty(row => row.HoldReason, reason),
                                                          TestContext.Current.CancellationToken));

            await watcher.LookAsync(TestContext.Current.CancellationToken);
        }

        await WithDbAsync(db => db.PrintFilesOnPrinters
                                  .Where(row => row.PrinterId == _printer.Id)
                                  .ExecuteUpdateAsync(set => set.SetProperty(row => row.HoldReason, PrintHoldReason.FileExistsUnknownSize),
                                                      TestContext.Current.CancellationToken));

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 1);

        heard.Keys.Should().BeEquivalentTo(["owner"]);
        _pushService.Received.Should().ContainSingle("the hold came back three times and was news once");
    }

    /// <summary>
    /// The countdown crossing five minutes, as the telemetry writer reports it, reaches everybody who may
    /// see the printer - with the time left counted in the reader's language.
    /// </summary>
    [Fact]
    public async Task AFilamentChangeComingReachesEverybodyWhoMaySeeThePrinter()
    {
        FilamentChangeWatch watch = _factory.Services.GetRequiredService<FilamentChangeWatch>();

        watch.Observed(_printer.Id,
                       new LiveStateSnapshot(PrinterStatus.Printing, null, null, 42, 330),
                       new LiveStateSnapshot(PrinterStatus.Printing, null, null, 42, 250),
                       DateTimeOffset.UtcNow);

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 3);

        heard.Keys.Should().BeEquivalentTo(["owner", "viewer", "muted"],
                                           "somebody who muted attention and holds still hears of a filament change");
        heard["owner"].GetProperty("title").GetString().Should().Be("Core One+ stopper snart for et filamentskift");
        heard["owner"].GetProperty("body").GetString().Should().Be("Om cirka 5 minutter.");
        heard["viewer"].GetProperty("body").GetString().Should().Be("In about 5 minutes.");
    }

    [Fact]
    public async Task APrinterLostMidPrintReachesEverybodyWhoMaySeeIt()
    {
        long jobId = 0;

        await WithDbAsync(async db =>
        {
            PrintJob job = new()
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printer.Id,
                FileName = "benchy.bgcode",
                QueuedByUserId = _owner,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                State = PrintState.Printing,
            };

            db.PrintJobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
        });

        // What the watcher publishes once the printer has been quiet for the whole grace, which a test
        // does not wait out; the watcher's own counting is tested against a clock it can move.
        _factory.Services.GetRequiredService<NotificationQueue>().Publish(new PrinterLost(_printer.Id, jobId));

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 3);

        heard.Keys.Should().BeEquivalentTo(["owner", "viewer", "muted"]);
        heard["owner"].GetProperty("title").GetString().Should().Be("Mistet forbindelsen til Core One+");
        heard["viewer"].GetProperty("body").GetString().Should().Be("It was printing benchy.bgcode – it may still be.");
    }

    /// <summary>
    /// A hold that was gone by the time it was composed reached nobody, and must not use up the gap:
    /// the real hold after it is heard. Found by the flapping test failing under load, where the
    /// dispatcher composed each hold after the test had already cleared it.
    /// </summary>
    [Fact]
    public async Task AHoldGoneBeforeItWasComposedDoesNotSilenceTheNextOne()
    {
        NotificationQueue queue = _factory.Services.GetRequiredService<NotificationQueue>();

        await WithDbAsync(async db =>
        {
            db.QueuedPrints.Add(new QueuedPrint
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printer.Id,
                PrintFileId = _fileId,
                Position = 1,
                QueuedByUserId = _owner,
                QueuedByScope = CapabilitySet.Format([Capability.Print]),
            });

            db.PrintFilesOnPrinters.Add(new PrintFileOnPrinter { PrinterId = _printer.Id, PrintFileId = _fileId });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        // Announced, but by the time it is composed there is no hold to describe.
        queue.Publish(new QueueHeld(_printer.Id, PrintHoldReason.FileExistsUnknownSize));
        await Task.Delay(NotificationWatcher.Interval, TestContext.Current.CancellationToken);
        _pushService.Received.Should().BeEmpty("there was nothing to say");

        await WithDbAsync(db => db.PrintFilesOnPrinters
                                  .Where(row => row.PrinterId == _printer.Id)
                                  .ExecuteUpdateAsync(set => set.SetProperty(row => row.HoldReason, PrintHoldReason.FileExistsUnknownSize),
                                                      TestContext.Current.CancellationToken));

        queue.Publish(new QueueHeld(_printer.Id, PrintHoldReason.FileExistsUnknownSize));

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 1);

        heard.Keys.Should().BeEquivalentTo(["owner"]);
    }

    [Fact]
    public async Task AFinishedPrintReachesWhoeverQueuedItAndNobodyElse()
    {
        await EndPrintAsync(PrintState.Finished, stoppedBy: null);

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 1);

        heard.Keys.Should().BeEquivalentTo(["owner"]);
        heard["owner"].GetProperty("title").GetString().Should().Be("Core One+ er færdig med at printe");
        heard["owner"].GetProperty("body").GetString().Should().Be("benchy.bgcode");
    }

    [Fact]
    public async Task APrintStoppedBySomebodyElseIsNewsAndOneStoppedByItsOwnerIsNot()
    {
        await EndPrintAsync(PrintState.Stopped, stoppedBy: _owner);
        await EndPrintAsync(PrintState.Stopped, stoppedBy: _viewer);

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 1);

        heard.Keys.Should().BeEquivalentTo(["owner"]);
        heard["owner"].GetProperty("title").GetString().Should().Be("Core One+ stoppede printet");
        _pushService.Received.Should().ContainSingle("the owner stopping their own print told them nothing new");
    }

    /// <summary>
    /// A held queue reaches those who may see the queue, told by the page's own sentence - so the
    /// viewer, who may see the printer but not its queue, hears nothing.
    /// </summary>
    [Fact]
    public async Task AHeldQueueReachesThoseWhoMaySeeTheQueue()
    {
        await WithDbAsync(async db =>
        {
            db.QueuedPrints.Add(new QueuedPrint
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printer.Id,
                PrintFileId = _fileId,
                Position = 1,
                QueuedByUserId = _owner,
                QueuedByScope = CapabilitySet.Format([Capability.Print]),
            });

            db.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
            {
                PrinterId = _printer.Id,
                PrintFileId = _fileId,
                HoldReason = PrintHoldReason.FileExistsUnknownSize,
                BlockedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        Dictionary<string, JsonElement> heard = await HeardAsync(expected: 1);

        heard.Keys.Should().BeEquivalentTo(["owner"]);
        heard["owner"].GetProperty("title").GetString().Should().Be("Køen på Core One+ venter");
        heard["owner"].GetProperty("body").GetString().Should().Contain("benchy.bgcode", "the hold is told in the page's own words");
    }

    /// <summary>
    /// Closing an account takes its browsers with it, as it takes its tokens - so nothing a stolen
    /// session added goes on hearing, even after the account is reopened.
    /// </summary>
    [Fact]
    public async Task ClosingAnAccountRemovesItsBrowsers()
    {
        (HSUser admin, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);
        client.Dispose();

        using IServiceScope scope = _factory.Services.CreateScope();

        UserAdminResult result = await scope.ServiceProvider.GetRequiredService<UserAdministration>()
                                            .DeactivateAsync(admin.Id, _viewer, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();

        (await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                    .NotificationDestinations
                    .CountAsync(destination => destination.UserId == _viewer, TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    private async Task<long> PersonAsync(string name)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, $"{name}@example.com");
        client.Dispose();

        FakePushBrowser browser = FakePushService.NewBrowser();
        _browsers[name] = browser;

        await WithDbAsync(async db =>
        {
            db.WebPushDestinations.Add(new WebPushDestination
            {
                UserId = user.Id,
                Endpoint = browser.Endpoint,
                P256dh = browser.P256dh,
                Auth = browser.Auth,
                Name = name,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        return user.Id;
    }

    private Task EndPrintAsync(PrintState state, long? stoppedBy)
    {
        return WithDbAsync(async db =>
        {
            db.PrintJobs.Add(new PrintJob
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printer.Id,
                FileName = "benchy.bgcode",
                QueuedByUserId = _owner,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                EndedAt = DateTimeOffset.UtcNow,
                State = state,
                StoppedByUserId = stoppedBy,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
    }

    private async Task WithDbAsync(Func<HomespoolDbContext, Task> action)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        await action(scope.ServiceProvider.GetRequiredService<HomespoolDbContext>());
    }

    /// <summary>
    /// Waits for <paramref name="expected"/> pushes, then a little longer for any that should not come,
    /// and answers what each person's browser read.
    /// </summary>
    private async Task<Dictionary<string, JsonElement>> HeardAsync(int expected)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow + Patience;

        while (_pushService.Received.Count < expected && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // Long enough for the watcher to look again and the dispatcher to send anything more.
        await Task.Delay(NotificationWatcher.Interval * 2, TestContext.Current.CancellationToken);

        Dictionary<string, JsonElement> heard = [];

        foreach (FakePush push in _pushService.Received)
        {
            (string name, FakePushBrowser browser) = _browsers.Single(pair => pair.Value.Endpoint == push.Endpoint.ToString());
            heard[name] = browser.DecryptJson(push.Body);
        }

        return heard;
    }
}
