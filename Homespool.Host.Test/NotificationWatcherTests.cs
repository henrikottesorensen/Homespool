using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Notifications;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What the watcher finds in the committed rows: each end once, a guess never, a hold when it is new -
/// and nothing that was already so when it started.
/// </summary>
public sealed class NotificationWatcherTests : IAsyncLifetime
{
    private readonly string _databasePath = WebPushRig.NewDatabasePath();
    private WebPushRig _rig = null!;
    private int _printerId;
    private long _userId;
    private long _fileId;

    public async ValueTask InitializeAsync()
    {
        _rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider());

        HSUser user = await _rig.AddUserAsync("owner@example.com");
        _userId = user.Id;

        await WithDbAsync(async db =>
        {
            Team team = new() { Name = "Workshop" };
            db.Teams.Add(team);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id, Name = "Core One" };
            db.Printers.Add(printer);

            HSFile file = new() { UserId = _userId, Name = "benchy.bgcode", Size = 1024 };
            db.Files.Add(file);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            _printerId = printer.Id;
            _fileId = file.Id;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        WebPushRig.Delete(_databasePath);
    }

    private async Task WithDbAsync(Func<HomespoolDbContext, Task> action)
    {
        using IServiceScope scope = _rig.Services.CreateScope();

        await action(scope.ServiceProvider.GetRequiredService<HomespoolDbContext>());
    }

    private Task AddEndedJobAsync(PrintState state, DateTimeOffset endedAt)
    {
        return WithDbAsync(async db =>
        {
            db.PrintJobs.Add(new PrintJob
            {
                PrintUuid = Guid.NewGuid(),
                PrinterId = _printerId,
                FileName = "benchy.bgcode",
                QueuedByUserId = _userId,
                StartedAt = endedAt.AddHours(-1),
                EndedAt = endedAt,
                State = state,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
    }

    private Task SetHoldAsync(PrintHoldReason? reason)
    {
        return WithDbAsync(async db =>
        {
            FileOnPrinter? row = await db.FilesOnPrinters.SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            if (row is null)
            {
                row = new FileOnPrinter { PrinterId = _printerId, FileId = _fileId };
                db.FilesOnPrinters.Add(row);
            }

            row.HoldReason = reason;
            row.BlockedAt = reason is null ? null : DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
    }

    private async Task<List<PrinterHappening>> LookAsync()
    {
        await _rig.Services.GetRequiredService<NotificationWatcher>().LookAsync(TestContext.Current.CancellationToken);

        List<PrinterHappening> published = [];
        NotificationQueue queue = _rig.Services.GetRequiredService<NotificationQueue>();

        while (queue.Reader.TryRead(out PrinterHappening? happening))
        {
            published.Add(happening);
        }

        return published;
    }

    [Fact]
    public async Task EachEndIsAnnouncedOnceAndAGuessNever()
    {
        await LookAsync();

        await AddEndedJobAsync(PrintState.Finished, DateTimeOffset.UtcNow);
        await AddEndedJobAsync(PrintState.Stopped, DateTimeOffset.UtcNow);
        await AddEndedJobAsync(PrintState.Failed, DateTimeOffset.UtcNow);
        await AddEndedJobAsync(PrintState.Unknown, DateTimeOffset.UtcNow);

        List<PrinterHappening> first = await LookAsync();
        List<PrinterHappening> second = await LookAsync();

        first.Should().HaveCount(3, "finished, stopped and failed are news; a row closed as Unknown is a guess")
             .And.AllBeOfType<PrintEnded>();
        second.Should().BeEmpty("an end already announced is not announced again");
    }

    /// <summary>
    /// An end committed in the same millisecond the watcher started. The column keeps milliseconds,
    /// so the row reads back a fraction before a watermark kept any finer - found by the query, then
    /// forgotten by the pruning, then announced again by the next look.
    /// </summary>
    [Fact]
    public async Task AnEndInTheMillisecondTheWatcherStartedIsAnnouncedOnce()
    {
        DateTimeOffset started = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        Microsoft.Extensions.Time.Testing.FakeTimeProvider clock = new(started.AddTicks(5_000));

        string path = WebPushRig.NewDatabasePath();

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(path, new EphemeralDataProtectionProvider(), time: clock);

            HSUser user = await rig.AddUserAsync("owner@example.com");
            NotificationWatcher watcher = rig.Services.GetRequiredService<NotificationWatcher>();
            NotificationQueue queue = rig.Services.GetRequiredService<NotificationQueue>();

            await rig.InScopeAsync(async services =>
            {
                HomespoolDbContext db = services.GetRequiredService<HomespoolDbContext>();
                Team team = new() { Name = "Workshop" };
                db.Teams.Add(team);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
                db.Printers.Add(printer);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                db.PrintJobs.Add(new PrintJob
                {
                    PrintUuid = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    FileName = "benchy.bgcode",
                    QueuedByUserId = user.Id,
                    StartedAt = started.AddHours(-1),
                    EndedAt = started,
                    State = PrintState.Finished,
                });

                return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });

            int announced = 0;

            for (int look = 0; look < 3; look++)
            {
                await watcher.LookAsync(TestContext.Current.CancellationToken);

                while (queue.Reader.TryRead(out _))
                {
                    announced++;
                }
            }

            announced.Should().Be(1);
        }
        finally
        {
            WebPushRig.Delete(path);
        }
    }

    /// <summary>
    /// A printer that goes quiet with a print open is announced once it has been quiet for the whole
    /// grace, once per print - and a reconnect inside the grace starts it again.
    /// </summary>
    [Fact]
    public async Task APrinterQuietForTheWholeGraceWithAPrintOpenIsLostOnce()
    {
        Microsoft.Extensions.Time.Testing.FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        string path = WebPushRig.NewDatabasePath();

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(path, new EphemeralDataProtectionProvider(), time: clock);

            HSUser user = await rig.AddUserAsync("owner@example.com");
            NotificationWatcher watcher = rig.Services.GetRequiredService<NotificationWatcher>();
            NotificationQueue queue = rig.Services.GetRequiredService<NotificationQueue>();
            Printing.PrinterConnectionRegistry connections = rig.Services.GetRequiredService<Printing.PrinterConnectionRegistry>();

            (int printerId, long jobId) = await rig.InScopeAsync(async services =>
            {
                HomespoolDbContext db = services.GetRequiredService<HomespoolDbContext>();
                Team team = new() { Name = "Workshop" };
                db.Teams.Add(team);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
                db.Printers.Add(printer);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                PrintJob job = new()
                {
                    PrintUuid = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    FileName = "benchy.bgcode",
                    QueuedByUserId = user.Id,
                    StartedAt = clock.GetUtcNow(),
                    State = PrintState.Printing,
                };

                db.PrintJobs.Add(job);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                return (printer.Id, job.Id);
            });

            async Task<int> LookAfterAsync(TimeSpan elapsed)
            {
                clock.Advance(elapsed);
                await watcher.LookAsync(TestContext.Current.CancellationToken);

                int published = 0;

                while (queue.Reader.TryRead(out PrinterHappening? happening))
                {
                    happening.Should().Be(new PrinterLost(printerId, jobId));
                    published++;
                }

                return published;
            }

            (await LookAfterAsync(TimeSpan.Zero)).Should().Be(0, "quiet from now");
            (await LookAfterAsync(NotificationWatcher.LostAfter / 2)).Should().Be(0);

            // Back briefly: the grace starts again from the next time it is missed.
            IPrinterLinkStub link = new();
            connections.Register(printerId, link, overPlaintext: false);
            (await LookAfterAsync(TimeSpan.FromSeconds(3))).Should().Be(0);
            connections.Unregister(printerId, link);

            (await LookAfterAsync(TimeSpan.FromSeconds(3))).Should().Be(0);
            (await LookAfterAsync(NotificationWatcher.LostAfter - TimeSpan.FromSeconds(1))).Should().Be(0, "a second short of the grace");
            (await LookAfterAsync(TimeSpan.FromSeconds(1))).Should().Be(1);
            (await LookAfterAsync(NotificationWatcher.LostAfter * 3)).Should().Be(0, "once per print");
        }
        finally
        {
            WebPushRig.Delete(path);
        }
    }

    [Fact]
    public async Task APrintThatEndedBeforeTheStartIsNotAnnounced()
    {
        await AddEndedJobAsync(PrintState.Finished, DateTimeOffset.UtcNow.AddMinutes(-10));

        (await LookAsync()).Should().BeEmpty("a restart does not replay what ended while it was down");
    }

    [Fact]
    public async Task AHoldIsAnnouncedWhenItAppearsAndNotWhileItLasts()
    {
        await SetHoldAsync(PrintHoldReason.InsufficientSpace);

        (await LookAsync()).Should().BeEmpty("a hold that was already there when the watcher started is not news");

        await SetHoldAsync(null);
        (await LookAsync()).Should().BeEmpty();

        await SetHoldAsync(PrintHoldReason.TransferRefused);
        (await LookAsync()).Should().ContainSingle()
                           .Which.Should().Be(new QueueHeld(_printerId, PrintHoldReason.TransferRefused));

        // Re-confirmed on every pass while it lasts - BlockedAt moves, the hold does not.
        await SetHoldAsync(PrintHoldReason.TransferRefused);
        (await LookAsync()).Should().BeEmpty();

        await SetHoldAsync(PrintHoldReason.InsufficientSpace);
        (await LookAsync()).Should().ContainSingle("a different reason is a different thing to sort out");
    }

    /// <summary>A connection that is open and does nothing, for the registry to count as connected.</summary>
    private sealed class IPrinterLinkStub : Printing.IPrinterLink
    {
        public bool IsOpen => true;

        public System.Threading.Tasks.Task<Printing.CommandSendResult> SendAsync(Printing.IPrinterIntent intent,
                                                                              System.Threading.CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void Complete()
        {
        }
    }
}
