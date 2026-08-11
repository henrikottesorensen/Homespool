using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TelemetryWriter"/> - the channel-fed background service that turns what
/// <see cref="MessageDispatcher"/> parses into rows in <see cref="PrinterLiveState"/>,
/// <see cref="TelemetrySample"/> and <see cref="PrinterEvent"/>.
/// </summary>
/// <remarks>
/// Run against real SQLite, matching every other persistence-touching suite in this project - the
/// upsert-vs-duplicate and new-slot-vs-existing-slot tests below specifically exercise EF's
/// generated SQL, which an in-memory provider would not catch drifting from.
/// </remarks>
public sealed class TelemetryWriterTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-telemetry-{Guid.NewGuid():N}.db");

    // A capturing rather than null logger: a flush failure is caught and logged rather than crashing
    // the writer (see TelemetryWriter.SafeFlushAsync), and a real logger here is what makes that
    // visible while debugging a test failure instead of silently swallowed. FakeLogger keeps each
    // entry structured, so the assertions below can check the level as a LogLevel and read the
    // writer's own log properties, rather than substring-matching one flattened string.
    private readonly FakeLogger<TelemetryWriter> _fakeLogger = new();

    private ServiceProvider? _provider;
    private TelemetryWriter? _writer;

    /// <summary>Every entry logged so far, newest last.</summary>
    private IReadOnlyList<FakeLogRecord> LogRecords => _fakeLogger.Collector.GetSnapshot();

    /// <summary>
    /// Polls rather than sleeping a fixed duration - the writer's drain loop runs on its own task, so
    /// there is no single moment a test can await to know a flush has happened.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static async Task<int> SampleCountAsync(HomespoolDbContext context)
    {
        return await context.TelemetrySamples.CountAsync();
    }

    /// <summary>
    /// Feeds one item at a time until <paramref name="observed"/> holds or the deadline passes.
    /// </summary>
    /// <remarks>
    /// The buffer-cap tests run at <c>WriteBatchSize: 1</c>, where every drained item triggers its
    /// own flush - and in those tests every flush fails, which costs an EF exception each time. How
    /// many items reach the buffer during a fixed-size burst therefore depends on how fast the
    /// machine throws exceptions, and the burst is over long before a cold drain loop has caught up.
    /// Feeding until the effect appears removes that dependence; a fixed burst made these tests pass
    /// only when the rest of the suite had already warmed EF and SQLite.
    /// </remarks>
    private static async Task<bool> FeedUntilAsync(Action enqueueOne, Func<bool> observed, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (observed())
            {
                return true;
            }

            enqueueOne();

            // Paced, not blasted: the channel's own capacity is 4 here, so an unbroken burst would
            // mostly self-inflict DropOldest before the reader ever sees it.
            await Task.Delay(2);
        }

        return observed();
    }

    /// <summary>
    /// A failed flush, which <see cref="TelemetryWriter"/> catches and logs rather than letting it
    /// kill the service. Requires the exception to be attached, not just matching text - a flush
    /// failure with no exception would mean the writer swallowed the cause.
    /// </summary>
    private static bool FlushFailed(FakeLogRecord record)
    {
        return record.Level == LogLevel.Error
               && record.Exception is not null
               && record.Message.Contains("flush failed");
    }

    private static StorageOptions DefaultOptions(int batchSize = 500, double flushIntervalSeconds = 30, double throttleSeconds = 0)
    {
        return new()
        {
            WriteBatchSize = batchSize,
            WriteFlushIntervalSeconds = flushIntervalSeconds,
            MinimumSampleIntervalSeconds = throttleSeconds,
        };
    }

    /// <summary>
    /// True once an entry matching <paramref name="predicate"/> has been logged. Polled, because the
    /// writer logs from its own drain loop - there is no moment a test can await directly.
    /// </summary>
    private Task<bool> LoggedAsync(Func<FakeLogRecord, bool> predicate)
    {
        return WaitUntilAsync(() => Task.FromResult(LogRecords.Any(predicate)), TimeSpan.FromSeconds(5));
    }

    /// <summary>Renders the captured log for a failure message.</summary>
    private string LogDump()
    {
        return string.Join('\n', LogRecords.Select(r => $"{r.Level}: {r.Message}"));
    }

    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "IDisposable.Dispose cannot be asynchronous, and the service must be stopped before the test's resources are torn down.")]
    public void Dispose()
    {
        if (_writer is not null)
        {
            // BackgroundService.StopAsync is idempotent and safe even if the service was never
            // started or already stopped, which some tests below do explicitly themselves.
            _writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _writer.Dispose();
        }

        _provider?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Builds and starts a writer against a fresh <see cref="IServiceScopeFactory"/> pointed at this
    /// test's database file - the same relationship <c>Program.cs</c> wires in production, minus
    /// everything else the host registers.
    /// </summary>
    private async Task<TelemetryWriter> StartWriterAsync(StorageOptions options,
                                                         TimeSpan? trimWarningInterval = null)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope migrationScope = _provider.CreateAsyncScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<HomespoolDbContext>().Database
                                .MigrateAsync(TestContext.Current.CancellationToken);
        }

        _writer = new TelemetryWriter(_provider.GetRequiredService<IServiceScopeFactory>(),
                                      Options.Create(options),
                                      _fakeLogger,
                                      TimeProvider.System,
                                      new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance))
        {
            SampleTrimWarningInterval = trimWarningInterval ?? TimeSpan.FromSeconds(10),
            EventTrimWarningInterval = trimWarningInterval ?? TimeSpan.FromSeconds(10),
        };

        await _writer.StartAsync(CancellationToken.None);

        return _writer;
    }

    private HomespoolDbContext NewVerificationContext()
    {
        return new(new DbContextOptionsBuilder<HomespoolDbContext>().UseSqlite($"Data Source={_databasePath}").Options);
    }

    /// <summary>
    /// <see cref="PrinterLiveState"/> and <see cref="TelemetrySample"/> both carry a required FK to
    /// <see cref="Printer"/>, enforced by SQLite - the writer only ever sees a <c>printerId</c> the
    /// auth handler already resolved against a real enrolled printer, so every test needs one to
    /// exist before enqueuing anything.
    /// </summary>
    private async Task SeedPrinterAsync(int printerId = 1)
    {
        await using HomespoolDbContext context = NewVerificationContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(new Printer
        {
            Id = printerId,
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Removes the printer row so any flush referencing it violates a foreign key, and returns an
    /// action that puts it back - a failure that can be switched off, rather than a permanent one.
    /// </summary>
    private async Task<Func<Task>> BreakThePrinterRowAsync(int printerId = 1)
    {
        await using HomespoolDbContext context = NewVerificationContext();

        Printer printer = await context.Printers.SingleAsync(p => p.Id == printerId, TestContext.Current.CancellationToken);
        int teamId = printer.TeamId;
        Guid uuid = printer.Uuid;

        context.Printers.Remove(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return async () =>
        {
            await using HomespoolDbContext restore = NewVerificationContext();

            restore.Printers.Add(new Printer
            {
                Id = printerId,
                Uuid = uuid,
                Type = PrinterType.PrusaConnect,
                TeamId = teamId,
                Status = PrinterStatus.Unknown,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await restore.SaveChangesAsync(TestContext.Current.CancellationToken);
        };
    }

    /// <summary>
    /// A shutdown flush that fails once still saves the buffers on a later attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shutdown flush is the only one with nothing behind it. While running, a failure costs
    /// nothing - the buffers are kept and the timer comes round again seconds later - but once the
    /// drain loop has exited there is no next attempt, so a single transient failure took everything
    /// buffered with it. Suspected cause of an intermittent failure of
    /// <see cref="EveryQueuedItemSurvivesShutdownNotJustTheFirst"/>, which asserts 25 samples and
    /// occasionally saw 0: exactly the shape of one lost flush.
    /// </para>
    /// <para>
    /// The failure here is switched off part-way through rather than simulated with timing: the
    /// printer row is removed so the flush violates a foreign key, then restored once the writer has
    /// logged its first failed attempt. Two retries remain after that, so a slow restore costs
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShutdownFlushThatFailsOnceStillSavesTheBuffer()
    {
        // Arrange - nothing flushes until shutdown: the batch size is unreachable and the timer long.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1000, flushIntervalSeconds: 30));
        await SeedPrinterAsync();

        for (int i = 0; i < 25; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow.AddSeconds(i),
                           new TelemetryDTO { Status = "PRINTING", Progress = i });
        }

        Func<Task> restorePrinter = await BreakThePrinterRowAsync();

        // Act - stop while the database will reject the write, then repair it mid-retry.
        Task stopping = writer.StopAsync(CancellationToken.None);

        bool firstAttemptFailed = await LoggedAsync(record =>
                                                        record.Level == LogLevel.Warning &&
                                                        record.Message.Contains("during shutdown"));

        firstAttemptFailed.Should().BeTrue($"the arrangement depends on the first shutdown flush failing.\n{LogDump()}");

        await restorePrinter();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();

        (await SampleCountAsync(verify)).Should().Be(25,
                                                     $"a transient failure at shutdown must not lose the buffer - there is no later flush to save it.\n{LogDump()}");
    }

    /// <summary>
    /// A runtime flush failure, once its cause is repaired, must not leave the writer wedged:
    /// everything still buffered lands on a later flush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins the fix for a permanent wedge the slow-database rig found (2026-07-29,
    /// notes/fake-printer-harness.md): the buffers survive a failed flush by design, but the failed
    /// flush's relationship fix-up had written its tracked <see cref="Printer"/> stub onto every
    /// buffered row's navigation property. The next flush re-tracked that dead context's stub via
    /// the navigation, collided with its own fresh stub ("another instance with the same key value
    /// is already being tracked"), and threw before reaching the database - every flush from then
    /// on, including after the database recovered, including the shutdown drain. The fix removed
    /// the <c>Printer</c> navigations from <see cref="TelemetrySample"/>, <see cref="PrinterEvent"/>
    /// and <see cref="PrinterLiveState"/> entirely, so fix-up has nothing to write onto.
    /// </para>
    /// <para>
    /// The <c>Material</c> value is the essential ingredient, not decoration: its writeback is what
    /// attaches a <see cref="Printer"/> stub into the failing flush's context, arming the fix-up.
    /// One failure with a material pending was enough to wedge permanently.
    /// </para>
    /// <para>
    /// <b>Mutation check:</b> re-adding <c>virtual Printer? Printer</c> to
    /// <see cref="TelemetrySample"/> (with the matching <c>HasOne(e => e.Printer)</c>
    /// configuration) must make this test fail. Guarding that regression is this test's whole
    /// purpose - the type system no longer prevents it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFlushFailureDoesNotWedgeTheWriterOnceTheDatabaseRecovers()
    {
        // Arrange - a fast timer, so recovery after the repair needs no further ingestion to trigger
        // it; a batch size the burst below reaches, so the first failure happens promptly too.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 5, flushIntervalSeconds: 0.1));
        await SeedPrinterAsync();

        Func<Task> restorePrinter = await BreakThePrinterRowAsync();

        for (int i = 0; i < 5; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow.AddSeconds(i),
                           new TelemetryDTO { Status = "PRINTING", Progress = i, Material = "PLA" });
        }

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow,
                       new EventDTO { EventType = Events.StateChanged, Status = "PRINTING" });

        bool firstAttemptFailed = await LoggedAsync(FlushFailed);
        firstAttemptFailed.Should().BeTrue(
            $"the arrangement depends on at least one flush failing while the printer row is missing.\n{LogDump()}");

        // Act - repair the database and let the timer retry.
        await restorePrinter();

        // Assert - the samples and the event both land. With the navigation bug, no flush ever
        // succeeds again: each retry throws while building the change-tracker graph, whether or not
        // the database is healthy.
        bool recovered = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext verify = NewVerificationContext();

            return await SampleCountAsync(verify) == 5
                   && await verify.PrinterEvents.CountAsync() == 1;
        }, TimeSpan.FromSeconds(10));

        recovered.Should().BeTrue(
            $"a flush failure must be survivable: once the database accepts writes again, the buffered rows must land.\n{LogDump()}");
    }

    /// <summary>
    /// While flushes are failing, the batch-size trigger must not fire for every further message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failing flush leaves the buffers above <c>WriteBatchSize</c> permanently, so the trigger
    /// condition holds for every item from then on - one full attempt, one exception and one
    /// Error-with-stack per message ingested, plus two more Errors from EF's internals. Measured
    /// before the guard: ~8.9 KB of log per attempt at 22 attempts/s during a 14 s outage
    /// (tools/slow-db), which is the same self-feeding shape that produced <c>LogThrottle</c>.
    /// Retrying once per message is also pure waste: nothing has changed since the attempt one
    /// message ago.
    /// </para>
    /// <para>
    /// <b>Mutation check:</b> dropping <c>&amp;&amp; _consecutiveFlushFailures == 0</c> from the
    /// trigger must fail this - the count goes from 1 to roughly the number of items fed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailingFlushIsNotRetriedForEveryFurtherMessage()
    {
        // Arrange - the timer is long enough never to fire here, so every attempt this test sees
        // came from the batch trigger and nothing else.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 5, flushIntervalSeconds: 30));
        await SeedPrinterAsync();
        await BreakThePrinterRowAsync();

        // Act - the first batch fails, which also proves the drain loop is alive and processing.
        for (int i = 0; i < 5; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow.AddSeconds(i),
                           new TelemetryDTO { Status = "PRINTING", Progress = i });
        }

        bool firstFailure = await LoggedAsync(FlushFailed);
        firstFailure.Should().BeTrue($"the arrangement depends on the first batch failing.\n{LogDump()}");

        // Feed well past the batch size, paced so the channel's own capacity does not shed them.
        for (int i = 0; i < 100; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow.AddSeconds(i),
                           new TelemetryDTO { Status = "PRINTING", Progress = i });
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert
        LogRecords.Count(FlushFailed).Should().Be(1,
                                                  $"a failing database must be retried on the flush timer, not once per message.\n{LogDump()}");
    }

    /// <summary>
    /// Trimming the sample buffer at its ceiling logs once per window, not once per message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TrimExcessPendingSamples</c> runs per sample processed, so at the cap every further
    /// message discards exactly one row and logged exactly one Warning saying so: 50,007 of them in
    /// one 180 s outage, each reporting a count of 1. The window summary is the aggregate the
    /// message was always phrased for.
    /// </para>
    /// <para>
    /// <b>Mutation check:</b> logging unconditionally instead of on an elected window must fail
    /// this, the count rising to one per trimmed row.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASampleTrimBurstInsideTheIntervalLogsExactlyOneWarning()
    {
        // Arrange - batch size 5 puts the sample ceiling at 100, reachable in a paced burst. A short
        // flush interval is what makes the buffer observable at all: PublishHealth only runs in
        // SafeFlushAsync's finally, so with a long interval a test is blind to how full the buffer
        // is and can only guess whether its burst arrived (it mostly does not - DropOldest sheds
        // whatever outruns the drain loop).
        TelemetryWriter writer = await StartWriterAsync(
            DefaultOptions(batchSize: 5, flushIntervalSeconds: 0.1), trimWarningInterval: TimeSpan.FromMinutes(10));
        await SeedPrinterAsync();
        await BreakThePrinterRowAsync();

        // Act - feed until the ceiling is actually reached, rather than assuming a fixed burst gets
        // there, then keep feeding: every sample past the cap discards a row and logged its own
        // Warning before the throttle.
        bool atCeiling = await FeedUntilAsync(
            () => writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Progress = 1 }),
            () => writer.Current.PendingSamples >= 100,
            TimeSpan.FromSeconds(20));

        atCeiling.Should().BeTrue(
            $"the arrangement depends on the sample ceiling being reached, saw {writer.Current.PendingSamples} pending.\n{LogDump()}");

        for (int i = 0; i < 100; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Progress = i });
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        bool trimmed = await LoggedAsync(record => record.Message.Contains("buffered telemetry sample"));
        trimmed.Should().BeTrue($"samples past the ceiling must trim, or this test proves nothing.\n{LogDump()}");

        // Assert
        LogRecords.Count(record => record.Message.Contains("buffered telemetry sample")).Should().Be(1,
            $"trims past the first must aggregate into a window summary, not flood the log.\n{LogDump()}");
    }

    /// <summary>
    /// The event-buffer trim is throttled the same way - but stays at Error, and every lost event is
    /// still counted exactly in the health snapshot.
    /// </summary>
    /// <remarks>
    /// Discarding an event is data loss with nothing to reconstruct it from, so the level is
    /// deliberately louder than the sample trim's and the first occurrence still logs in full. What
    /// is bounded is the repetition (5,193 identical Errors in one 180 s outage). The exact total
    /// rides on the snapshot, unthrottled, which is what alerting watches.
    /// <para>
    /// <b>Mutation check:</b> logging unconditionally fails the count assertion; publishing the
    /// window count rather than <c>LogThrottle.Total</c> fails the snapshot assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEventTrimBurstLogsOnceAndStillCountsEveryLostEventInHealth()
    {
        // Arrange - batch size 5 puts the event ceiling at 50. Short flush interval so the buffer is
        // observable while flushes fail; see the sample-trim test for why that matters.
        TelemetryWriter writer = await StartWriterAsync(
            DefaultOptions(batchSize: 5, flushIntervalSeconds: 0.1), trimWarningInterval: TimeSpan.FromMinutes(10));
        await SeedPrinterAsync();
        await BreakThePrinterRowAsync();

        // Act - feed until the ceiling is reached, then keep going: each event past it is one lost.
        bool atCeiling = await FeedUntilAsync(
            () => writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow,
                                 new EventDTO { EventType = Events.StateChanged, Status = "PRINTING" }),
            () => writer.Current.PendingEvents >= 50,
            TimeSpan.FromSeconds(20));

        atCeiling.Should().BeTrue(
            $"the arrangement depends on the event ceiling being reached, saw {writer.Current.PendingEvents} pending.\n{LogDump()}");

        for (int i = 0; i < 100; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow,
                           new EventDTO { EventType = Events.StateChanged, Status = "PRINTING" });
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        bool trimmed = await LoggedAsync(record => record.Message.Contains("buffered printer event"));
        trimmed.Should().BeTrue($"the arrangement depends on the event ceiling being reached.\n{LogDump()}");

        // Assert
        LogRecords.Count(record => record.Message.Contains("buffered printer event")).Should().Be(1,
            $"event trims past the first must aggregate into a window summary.\n{LogDump()}");

        LogRecords.Where(record => record.Message.Contains("buffered printer event"))
                  .Should().OnlyContain(record => record.Level == LogLevel.Error,
                                        "discarding an event is data loss, not degradation - the level must not soften with throttling");

        bool counted = await WaitUntilAsync(
            () => Task.FromResult(writer.Current.DiscardedEvents > 20),
            TimeSpan.FromSeconds(5));

        counted.Should().BeTrue(
            $"every discarded event must be counted in the snapshot however little is logged - one log line, hundreds of losses, saw {writer.Current.DiscardedEvents}.\n{LogDump()}");
    }

    [Fact]
    public async Task TelemetryIsPersistedOnceTheBatchSizeIsReached()
    {
        // Arrange - a large flush interval, so only the batch-size trigger could plausibly fire.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 30));
        await SeedPrinterAsync();

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue("a single item should flush immediately once it reaches the batch size");

        await using HomespoolDbContext verify = NewVerificationContext();
        PrinterLiveState state = await verify.PrinterLiveStates.SingleAsync(TestContext.Current.CancellationToken);
        state.PrinterId.Should().Be(1);
        state.Status.Should().Be(PrinterStatus.Printing);
    }

    [Fact]
    public async Task TelemetryIsPersistedOnATimerEvenBelowTheBatchSize()
    {
        // Arrange - a batch size no single message could ever reach, and a short timer.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1000, flushIntervalSeconds: 0.1));
        await SeedPrinterAsync();

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "IDLE" });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue("the periodic timer must flush a low-traffic printer even though its batch never fills");
    }

    /// <summary>
    /// Confirmed missing in practice before this was fixed: a real MK3.5 session's telemetry from an
    /// active print vanished across a dev-server restart, because the only flush triggers were the
    /// batch-size and timer thresholds - shutdown didn't necessarily hit either.
    /// </summary>
    [Fact]
    public async Task TelemetryIsFlushedOnGracefulShutdownEvenBelowBothThresholds()
    {
        // Arrange - neither the batch-size nor the timer could plausibly fire before shutdown.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1000, flushIntervalSeconds: 30));
        await SeedPrinterAsync();

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        // Act
        await writer.StopAsync(CancellationToken.None);

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();

        // See TelemetryIsFlushedOnGracefulShutdownEvenBelowBothThresholds for why this is checked:
        // a loop cancelled before it ever ran looks exactly like a clean stop from the call site.
        writer.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion,
                                               $"the drain loop must finish, not fault or cancel.\nLOG:\n{LogDump()}");

        (await SampleCountAsync(verify)).Should().Be(1,
                                                     $"shutdown must flush whatever is buffered rather than discarding it.\nLOG:\n{LogDump()}");
    }

    /// <summary>
    /// The stronger form of the test above: a backlog of both item kinds queued at the moment
    /// shutdown begins, none of it near either flush threshold. Nothing may be dropped - not the
    /// items already moved into the in-memory buffers, and not the ones still sitting unread in the
    /// channel. Shutdown-by-completion makes that one property rather than two separate rescues.
    /// </summary>
    [Fact]
    public async Task EveryQueuedItemSurvivesShutdownNotJustTheFirst()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1000, flushIntervalSeconds: 30));
        await SeedPrinterAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 25; i++)
        {
            writer.Enqueue(printerId: 1, now.AddSeconds(i), new TelemetryDTO { Status = "PRINTING", Progress = i });
        }

        writer.Enqueue(printerId: 1, now.AddSeconds(25), new EventDTO
        {
            EventType = Events.Finished,
            Status = "PRINTING",
            CommandId = 42,
        });

        // Act
        await writer.StopAsync(CancellationToken.None);

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();

        // BackgroundService.StopAsync waits via Task.WhenAny, which does not rethrow - so a drain
        // loop that died of an exception is indistinguishable from a clean stop at the call site,
        // and shows up only as missing rows. Checked explicitly, because that is exactly the shape
        // this test failed in: zero rows and an empty log.
        // See TelemetryIsFlushedOnGracefulShutdownEvenBelowBothThresholds for why this is checked:
        // a loop cancelled before it ever ran looks exactly like a clean stop from the call site.
        writer.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion,
                                               $"the drain loop must finish, not fault or cancel.\nLOG:\n{LogDump()}");

        LogRecords.Where(FlushFailed).Should().BeEmpty($"no flush should have failed.\n{LogDump()}");

        (await SampleCountAsync(verify)).Should().Be(25, $"everything queued must survive the drain.\n{LogDump()}");
        (await verify.PrinterEvents.CountAsync(TestContext.Current.CancellationToken)).Should()
                                                                                      .Be(
                                                                                          1,
                                                                                          $"an event queued at shutdown is a discrete fact that never repeats.\n{LogDump()}");

        // And the live state reflects the last message merged, not an arbitrary earlier one.
        PrinterLiveState state = await verify.PrinterLiveStates.SingleAsync(TestContext.Current.CancellationToken);
        state.Progress.Should().Be(24);
    }

    [Fact]
    public async Task TheThrottleSkipsTheSampleButTheLiveStateStillMerges()
    {
        // Arrange - a throttle window comfortably longer than the two messages are apart. The flush
        // interval is short (unlike the batch-size test above): a throttled message never adds to
        // pendingSamples, so its dirty live-state can only reach the database via the timer, not the
        // batch-size trigger.
        TelemetryWriter writer =
            await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.1, throttleSeconds: 3600));
        await SeedPrinterAsync();

        DateTimeOffset first = DateTimeOffset.UtcNow;

        // Act
        writer.Enqueue(printerId: 1, first, new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 200 });

        bool firstFlushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        firstFlushed.Should().BeTrue();

        writer.Enqueue(printerId: 1, first.AddSeconds(1), new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 210 });

        bool liveStateUpdated = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            return state?.NozzleTemperature == 210;
        }, TimeSpan.FromSeconds(5));

        // Assert
        liveStateUpdated.Should().BeTrue("the live view must reflect the newest message regardless of the throttle");

        await using HomespoolDbContext verify = NewVerificationContext();
        (await SampleCountAsync(verify)).Should().Be(1,
                                                     "the second message arrived inside the throttle window, so history density - not the live view - is what it skips");
    }

    [Fact]
    public async Task AnEventIsPersistedWithItsRawPayload()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse("""{"firmware":"6.4.0"}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info,
            Status = "IDLE",
            JobId = 42,
            Data = payload.RootElement.Clone(),
        });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterEvents.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        PrinterEvent stored = await verify.PrinterEvents.SingleAsync(TestContext.Current.CancellationToken);

        stored.PrinterId.Should().Be(1);
        stored.EventType.Should().Be(Events.Info);
        stored.Status.Should().Be(PrinterStatus.Idle);
        stored.JobId.Should().Be(42);
        stored.Payload.Should().Be("""{"firmware":"6.4.0"}""");
    }

    /// <summary>
    /// <c>INFO</c> carries <c>api_key</c> - <b>the printer's PrusaLink password</b>, which grants full
    /// authenticated access to its HTTP API, including reading any file off the drive. Firmware
    /// volunteers it on every connection, and this table is append-only with no retention sweep, so
    /// without this it accumulates in clear text beside the printer's own address.
    /// </summary>
    /// <remarks>
    /// Found in a live events table on 2026-07-31, having been written on every reconnection since
    /// enrolment. Rotating the password on the printer does not remove the copies already stored,
    /// which is exactly why the fix has to be at the write.
    /// </remarks>
    [Fact]
    public async Task AnInfoEventStoresItsApiKeyRedacted()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse(
            """
            {"firmware":"6.5.7+12836","api_key":"vC7x4aZfohmcbzH","nozzle_diameter":0.4,
             "network_info":{"wifi_ssid":"example-network","wifi_mac":"00:00:5E:00:53:2A","wifi_ipv4":"192.168.13.110","hostname":"prusa-mk35"},
             "storages":[{"mountpoint":"/usb","read_only":false}]}
            """);

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info,
            Status = "IDLE",
            Data = payload.RootElement.Clone(),
        });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterEvents.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        PrinterEvent stored = await verify.PrinterEvents.SingleAsync(TestContext.Current.CancellationToken);

        stored.Payload.Should().NotContain("vC7x4aZfohmcbzH", "the credential must never reach a row");
        stored.Payload.Should().NotContain("example-network", "an SSID names where someone lives");
        stored.Payload.Should().NotContain("00:00:5E:00:53:2A");

        using JsonDocument kept = JsonDocument.Parse(stored.Payload!);

        // Masked rather than dropped: a silently absent key reads as "firmware stopped sending it".
        kept.RootElement.GetProperty("api_key").GetString().Should().Be("[redacted]");

        JsonElement network = kept.RootElement.GetProperty("network_info");

        // Nested, so a flat name match would have missed both of these entirely.
        network.GetProperty("wifi_ssid").GetString().Should().Be("[redacted]");
        network.GetProperty("wifi_mac").GetString().Should().Be("[redacted]");

        // Everything else survives - a blacklist of three paths, not the FILE_INFO allowlist.
        kept.RootElement.GetProperty("firmware").GetString().Should().Be("6.5.7+12836");
        kept.RootElement.GetProperty("nozzle_diameter").GetDouble().Should().Be(0.4);
        network.GetProperty("wifi_ipv4").GetString().Should().Be("192.168.13.110");
        network.GetProperty("hostname").GetString().Should().Be("prusa-mk35");
        kept.RootElement.GetProperty("storages").EnumerateArray().Should().ContainSingle();
    }

    /// <summary>
    /// <c>INFO</c> is the only message saying what a printer <i>is</i>, and until this landed nothing
    /// consumed it: <c>Printer.Firmware</c> had no assignment anywhere in the codebase, so the API
    /// reported <c>null</c> for every printer forever.
    /// </summary>
    [Fact]
    public async Task AnInfoEventFillsInTheFirmwareAndModel()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse("""{"firmware":"6.5.7","printer_type":"1.3.5"}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info,
            Status = "IDLE",
            Data = payload.RootElement.Clone(),
        });

        // Assert
        bool applied = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware != null, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        applied.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer = await verify.Printers.SingleAsync(TestContext.Current.CancellationToken);

        printer.Firmware.Should().Be("6.5.7");
        printer.Model.Should().Be("1.3.5");
    }

    /// <summary>
    /// A firmware upgrade overwrites what was stored, and so does a model change - Prusa sell upgrade
    /// kits, so an MK3 genuinely becomes an MK3.5 under the same identity (Henrik, 2026-07-28).
    /// </summary>
    [Fact]
    public async Task LaterInfoOverwritesTheStoredFirmwareAndModel()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument before = JsonDocument.Parse("""{"firmware":"6.5.7","printer_type":"1.3.0"}""");
        using JsonDocument after = JsonDocument.Parse("""{"firmware":"6.6.3","printer_type":"1.3.5"}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = before.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware == "6.5.7", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = after.RootElement.Clone(),
        });

        // Assert
        bool upgraded = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware == "6.6.3", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        upgraded.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer = await verify.Printers.SingleAsync(TestContext.Current.CancellationToken);

        printer.Model.Should().Be("1.3.5", "an upgrade kit changes the model under the same identity");
    }

    /// <summary>
    /// The nozzle diameter is refreshed like firmware, not written once like the serial: people swap
    /// nozzles, so it describes the hardware as it stands rather than which machine this is.
    /// </summary>
    [Fact]
    public async Task ANozzleSwapIsPickedUpFromTheNextInfo()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument brass = JsonDocument.Parse("""{"nozzle_diameter":0.4}""");
        using JsonDocument swapped = JsonDocument.Parse("""{"nozzle_diameter":0.6}""");

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = brass.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            // A range, not equality: SQLite stores REAL as a double, so a widened float never
            // compares equal to the clean double EF sends as the parameter.
            return await context.Printers.AnyAsync(p => p.NozzleDiameter < 0.5f, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = swapped.RootElement.Clone(),
        });

        // Assert
        bool swappedIn = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.NozzleDiameter > 0.5f, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        swappedIn.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.Printers.SingleAsync(TestContext.Current.CancellationToken)).NozzleDiameter.Should()
                                                                                  .BeApproximately(0.6f, 0.001f);
    }

    /// <summary>
    /// A nozzle diameter survives the round trip through SQLite as the value the printer sent, and
    /// serialises as <c>0.4</c> rather than <c>0.40000000596046448</c>.
    /// </summary>
    /// <remarks>
    /// The concern is real and the guard is cheap: SQLite has no 4-byte float type, so the column is
    /// <c>REAL</c> and the stored bits are the double widening of 0.4f, which is
    /// 0.4000000059604645. What saves the output is that the property is <see cref="float"/> at both
    /// ends - EF narrows on read, and <see cref="System.Text.Json"/> formats a float with the
    /// shortest representation that round-trips, giving "0.4".
    /// <para>
    /// <b>Widen the property to <c>double</c> anywhere in the chain and this breaks</b>, because the
    /// widening artefact then becomes the shortest round-trippable form of a double. That is the
    /// mistake this test exists to catch, and it would be made in a DTO rather than here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANozzleDiameterSurvivesStorageWithoutFloatingPointLitter()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse("""{"nozzle_diameter":0.4}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = payload.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.NozzleDiameter != null, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer = await verify.Printers.SingleAsync(TestContext.Current.CancellationToken);

        printer.NozzleDiameter.Should().Be(0.4f, "EF narrows the stored double back to the float that was written");
        JsonSerializer.Serialize(printer.NozzleDiameter).Should().Be("0.4");

        // What the column actually holds, and why the two lines above are not redundant.
        ((double)printer.NozzleDiameter!.Value).Should().NotBe(0.4d);

        // The three ways of asking "is this 0.4?" in SQL, and which of them work. EF has no
        // approximate-equality helper, so the choice is between a range and Math.Abs - the latter
        // translating to SQLite's abs(). Equality is the one that silently matches nothing.
        (await verify.Printers.CountAsync(p => p.NozzleDiameter == 0.4f, TestContext.Current.CancellationToken))
            .Should().Be(0, "float equality against a REAL column matches nothing");

        (await verify.Printers.CountAsync(p => p.NozzleDiameter > 0.39f && p.NozzleDiameter < 0.41f,
                                          TestContext.Current.CancellationToken))
            .Should().Be(1, "a range is the simplest thing that works");

        (await verify.Printers.CountAsync(p => Math.Abs(p.NozzleDiameter!.Value - 0.4f) < 0.001f,
                                          TestContext.Current.CancellationToken))
            .Should().Be(1, "Math.Abs translates to abs() and is the closest thing to an epsilon compare");
    }

    /// <summary>
    /// An MMU announces itself through <c>INFO</c>'s <c>mmu.enabled</c>, which is
    /// <c>enabled_tool_cnt() &gt; 1</c> in firmware.
    /// </summary>
    [Fact]
    public async Task AnInfoEventRecordsThatAnMmuIsEnabled()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse("""{"mmu":{"enabled":true,"version":"3.0.3"}}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = payload.RootElement.Clone(),
        });

        // Assert
        bool applied = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.HasMmuEnabled, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        applied.Should().BeTrue();
    }

    /// <summary>
    /// <b>An INFO without an mmu block must not clear a stored true.</b> The block is absent on
    /// firmware built without MMU support, so absence means "cannot have one" - which the column's
    /// false default already says. Writing false on absence instead would let any partial INFO undo a
    /// genuine detection.
    /// </summary>
    [Fact]
    public async Task AnInfoEventWithoutAnMmuBlockLeavesTheStoredValueAlone()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument withMmu = JsonDocument.Parse("""{"mmu":{"enabled":true}}""");
        using JsonDocument without = JsonDocument.Parse("""{"firmware":"6.5.7"}""");

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = withMmu.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.HasMmuEnabled, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = without.RootElement.Clone(),
        });

        // The firmware in the second event is the marker that it was processed at all.
        bool processed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware == "6.5.7", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Assert
        processed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.Printers.SingleAsync(TestContext.Current.CancellationToken)).HasMmuEnabled.Should().BeTrue();
    }

    /// <summary>
    /// The serial number is filled in when missing. Before <see cref="Printer.SerialNumber"/> existed
    /// it was captured at registration and then discarded with the registration row, and a
    /// USB-provisioned printer never reported one at all.
    /// </summary>
    [Fact]
    public async Task AnInfoEventFillsInAMissingSerialNumber()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse("""{"firmware":"6.5.7","sn":"SN-12345"}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = payload.RootElement.Clone(),
        });

        // Assert
        bool applied = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.SerialNumber != null, TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        applied.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.Printers.SingleAsync(TestContext.Current.CancellationToken)).SerialNumber.Should().Be("SN-12345");
    }

    /// <summary>
    /// <b>A serial number that disagrees with the stored one is not acted on.</b> A different serial
    /// means a different machine, which arrives with a different fingerprint and is therefore a
    /// different row - so overwriting would corrupt this printer's identity rather than correct it
    /// (Henrik, 2026-07-28). Firmware and model, by contrast, are overwritten freely.
    /// </summary>
    [Fact]
    public async Task AnInfoEventNeverOverwritesAnExistingSerialNumber()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument first = JsonDocument.Parse("""{"sn":"SN-ORIGINAL"}""");
        using JsonDocument second = JsonDocument.Parse("""{"sn":"SN-DIFFERENT","firmware":"6.6.3"}""");

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = first.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.SerialNumber == "SN-ORIGINAL", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = second.RootElement.Clone(),
        });

        // The firmware in the same event is the marker that this INFO was processed at all - without
        // it the assertion below would pass simply by racing ahead of the flush.
        bool processed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware == "6.6.3", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Assert
        processed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.Printers.SingleAsync(TestContext.Current.CancellationToken)).SerialNumber.Should().Be("SN-ORIGINAL");
    }

    /// <summary>
    /// A field the firmware omits means "unknown", not "empty" - so an INFO without firmware must not
    /// erase what an earlier one established.
    /// </summary>
    [Fact]
    public async Task AnInfoEventWithoutFirmwareLeavesTheStoredValueAlone()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument full = JsonDocument.Parse("""{"firmware":"6.5.7","printer_type":"1.3.5"}""");
        using JsonDocument sparse = JsonDocument.Parse("""{"nozzle_diameter":0.4}""");

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = full.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.Printers.AnyAsync(p => p.Firmware == "6.5.7", TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.Info, Status = "IDLE", Data = sparse.RootElement.Clone(),
        });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterEvents.CountAsync() == 2;
        }, TimeSpan.FromSeconds(5));

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer = await verify.Printers.SingleAsync(TestContext.Current.CancellationToken);

        printer.Firmware.Should().Be("6.5.7");
        printer.Model.Should().Be("1.3.5");
    }

    /// <summary>
    /// A <c>FILE_INFO</c> keeps only what firmware itself renders. Everything else in that object is
    /// the uploaded gcode's own header - measured at 396 of 400 keys appearing verbatim in the file
    /// we already store, the other 4 being base64 thumbnail fragments mangled by firmware's
    /// <c>key = value</c> split.
    /// </summary>
    /// <remarks>
    /// An allowlist because the two sets have different shapes: firmware's is closed and enumerable
    /// from render.cpp, the gcode's is unbounded and attacker-influenced. The printer sends these
    /// unasked - three per transferred file, ~18 KB each, in a table retention never sweeps.
    /// </remarks>
    [Fact]
    public async Task AFileInfoKeepsOnlyWhatFirmwareRenders()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse(
            """
            {"preview":"iVBORw0KGgoAAAA","objects_info":"{\"objects\":[{\"name\":\"A\"}]}",
             "layer_height":0.2,"filament used [g]":"51.35","estimated printing time (normal mode)":"2h 51m",
             "iP5nSy8PNRc1GwE6w/9UVFSAPBk4":"","size":614400,"m_timestamp":1785189559,
             "read_only":false,"display_name":"model.gcode","type":"PRINT_FILE","path":"/usb/MODEL~1.GCO"}
            """);

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.FileInfo,
            Status = "IDLE",
            Data = payload.RootElement.Clone(),
        });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterEvents.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        PrinterEvent stored = await verify.PrinterEvents.SingleAsync(TestContext.Current.CancellationToken);

        using JsonDocument kept = JsonDocument.Parse(stored.Payload!);
        kept.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["size", "m_timestamp", "read_only", "display_name", "type", "path"],
            "only the fields render.cpp emits itself survive - preview included, since that one is "
            + "firmware-rendered but is pure gcode content");

        stored.Payload.Should().NotContain("iVBORw0KGgoAAAA").And.NotContain("layer_height");
        stored.Payload.Should().NotContain("iP5nSy8", "a blacklist could never have named this key");
        stored.Payload.Should().Contain("/usb/MODEL~1.GCO");
    }

    /// <summary>
    /// The allowlist applies to <c>FILE_INFO</c> and nothing else. Every other event type has its own
    /// field set, and reducing one of those to a FILE_INFO shape would empty it.
    /// </summary>
    /// <remarks>
    /// The payload here deliberately carries a <c>path</c> - a name on the allowlist - so the test
    /// fails if the filter is applied by field name instead of by event type, rather than passing by
    /// coincidence.
    /// </remarks>
    [Fact]
    public async Task AnEventThatIsNotAFileInfoIsUntouched()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument payload = JsonDocument.Parse(
            """{"start_cmd_id":42,"type":"FROM_CONNECT","path":"/usb/model.gcode","progress":12.5}""");

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO
        {
            EventType = Events.TransferInfo,
            Status = "IDLE",
            Data = payload.RootElement.Clone(),
        });

        // Assert
        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterEvents.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        PrinterEvent stored = await verify.PrinterEvents.SingleAsync(TestContext.Current.CancellationToken);

        stored.Payload.Should().Be(
            """{"start_cmd_id":42,"type":"FROM_CONNECT","path":"/usb/model.gcode","progress":12.5}""",
            "a non-FILE_INFO payload is stored exactly as it arrived");
    }

    [Fact]
    public async Task LiveStateUpsertsInPlaceAcrossFlushesRatherThanDuplicating()
    {
        // Arrange - a small batch size so each Enqueue flushes on its own, giving two distinct
        // flush cycles rather than one that happens to cover both messages.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "IDLE" });

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterLiveStates.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool secondFlushLanded = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            return state?.Status == PrinterStatus.Printing;
        }, TimeSpan.FromSeconds(5));

        // Assert
        secondFlushLanded.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.PrinterLiveStates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "a second flush for a printer already in the database must update its row, not insert another");
    }

    /// <summary>
    /// Regression guard for the exact failure the flush's explicit <c>EntityState</c> assignment
    /// exists to prevent: a slot reported for the first time in a <em>later</em> flush, on a printer
    /// whose live-state row already exists from an earlier one.
    /// </summary>
    /// <remarks>
    /// <see cref="PrinterLiveSlotState"/> keys on real (<see cref="Printer"/> id, slot number) values,
    /// never a CLR-default surrogate key, so asking EF to infer Added-vs-Modified from the key alone
    /// (what <c>DbSet.Update</c> does) cannot tell a brand-new slot from an existing one - guessing
    /// Modified for a slot that has never been saved issues an <c>UPDATE</c> matching zero rows,
    /// silently, rather than the <c>INSERT</c> the data needs.
    /// </remarks>
    [Fact]
    public async Task ASlotReportedForTheFirstTimeInALaterFlushIsPersisted()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        using JsonDocument firstMessage = JsonDocument.Parse(
            """{"state":"PRINTING","slot":{"active":1,"1":{"material":"PLA","temp":210,"fan_hotend":8000,"fan_print":6000}}}""");

        TelemetryDTO first = firstMessage.RootElement.Deserialize<TelemetryDTO>()!;

        // Act - first flush: printer and its one slot are both brand new.
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, first);

        await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterLiveSlotStates.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        using JsonDocument secondMessage = JsonDocument.Parse(
            """{"state":"PRINTING","slot":{"active":2,"2":{"material":"PETG","temp":230,"fan_hotend":8000,"fan_print":6000}}}""");

        TelemetryDTO second = secondMessage.RootElement.Deserialize<TelemetryDTO>()!;

        // Act - second flush, against a printer that already has a live-state row: slot 2 has never
        // been seen before now.
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, second);

        bool secondSlotPersisted = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await context.PrinterLiveSlotStates.CountAsync() == 2;
        }, TimeSpan.FromSeconds(5));

        // Assert
        secondSlotPersisted.Should()
                           .BeTrue("a slot seen for the first time in a later flush must be inserted, not silently dropped");

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.PrinterLiveSlotStates.Select(s => s.SlotNumber).OrderBy(n => n)
                     .ToListAsync(TestContext.Current.CancellationToken))
            .Should().Equal([1, 2], "the first flush's slot must survive, not be replaced by the second's");
    }

    [Fact]
    public async Task LoadedMaterialIsPopulatedOnThePrinterRow()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        // Act
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Material = "PLA" });

        // Assert
        bool populated = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            Printer printer = await context.Printers.SingleAsync();
            return printer.LoadedMaterial == "PLA";
        }, TimeSpan.FromSeconds(5));

        populated.Should().BeTrue();
    }

    /// <summary>
    /// A printer reporting a material keeps persisting across flushes, not just on the first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape the single-flush test above cannot see. A printing printer sends a material on
    /// <i>every</i> telemetry message, so <c>PendingLoadedMaterial</c> is set again on every drain and
    /// the <c>Printer</c>-stub branch in <c>FlushAsync</c> runs on every flush. Attaching that stub
    /// alongside the cached <see cref="PrinterLiveState"/> made EF fix up the relationship and write
    /// the stub onto <c>entry.State.Printer</c> - a cached object that outlives the per-flush
    /// context. The next flush then dragged that stale instance in with the live state and collided
    /// with the fresh stub: "another instance with the same key value is already being tracked".
    /// </para>
    /// <para>
    /// Permanent once it starts, and silent: <c>SafeFlushAsync</c> logs and keeps the buffers, so the
    /// writer goes on accepting telemetry it can never persist. Observed against a real MK3.5 on
    /// 2026-07-25 as 176 consecutive failures and 285 lost samples across one print.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MaterialKeepsPersistingAcrossFlushesNotJustTheFirst()
    {
        // Arrange - batch size 1, so each message is its own flush.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1));
        await SeedPrinterAsync();

        // Act - what a printing printer actually sends: every message carries the material.
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Material = "PLA" });

        bool firstFlushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        firstFlushed.Should().BeTrue("the first flush has never been the broken one");

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Material = "PETG" });

        // Assert
        bool secondFlushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 2;
        }, TimeSpan.FromSeconds(5));

        secondFlushed.Should().BeTrue($"every later flush must persist too, not just the first.\n{LogDump()}");

        LogRecords.Where(FlushFailed).Should().BeEmpty("no flush should have failed at all");

        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer = await verify.Printers.SingleAsync(TestContext.Current.CancellationToken);
        printer.LoadedMaterial.Should().Be("PETG", "the later material must reach the printer row as well");
    }

    [Fact]
    public async Task EnqueueNeverBlocksOrThrowsUnderHeavyLoad()
    {
        // Arrange - a tiny channel (batch size 1 -> capacity 4) and a slow-ish flush interval, so the
        // channel is under real pressure rather than draining as fast as it fills.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 1));
        await SeedPrinterAsync();

        // Act
        Action enqueueMany = () =>
        {
            for (int i = 0; i < 5000; i++)
            {
                writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });
            }
        };

        // Assert - DropOldest must mean Enqueue is always a non-blocking TryWrite; this would hang
        // (rather than throw) if that regressed to a blocking write against a full bounded channel.
        Task enqueueTask = Task.Run(enqueueMany, TestContext.Current.CancellationToken);
        Task completed =
            await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        completed.Should().Be(enqueueTask, "Enqueue must never block, even against a full channel");
        enqueueTask.IsFaulted.Should().BeFalse();
    }

    /// <summary>
    /// A dropped item must not vanish unremarked - it is real telemetry a printer sent that the
    /// server is choosing to discard, which is worth an operator's attention.
    /// </summary>
    [Fact]
    public async Task DroppingAnItemLogsAWarning()
    {
        // Arrange - batch size 1 -> channel capacity 4, so a fast burst overflows it well before
        // the single reader (which does real async work per item) can keep pace.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 30));

        // Act
        for (int i = 0; i < 200; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });
        }

        // Assert
        bool warned = await LoggedAsync(record => record.Level == LogLevel.Warning
                                                  && record.Message.Contains("Dropped")
                                                  && record.StructuredState!.Any(kv => kv.Key == "PrinterId" && kv.Value == "1"));

        warned.Should().BeTrue($"a full channel under DropOldest must be logged, not silently discarded. Log:\n{LogDump()}");
    }

    /// <summary>
    /// A sustained drop burst logs one warning, not one per drop. Per-drop logging failed its first
    /// load test: 20 seconds of blast telemetry produced 722,973 warnings and a 1.0 GB log - and
    /// because the callback runs on the producer's thread, the logging itself taxed the message path
    /// that was already overloaded (notes/fake-printer-harness.md, the blast run).
    /// </summary>
    [Fact]
    public void ADropBurstInsideTheWarningIntervalLogsExactlyOneWarning()
    {
        // Arrange - an unstarted writer never drains, so with batch size 1 (channel capacity 4)
        // every enqueue past the fourth is deterministically a drop, on this thread, synchronously.
        TelemetryWriter writer = CreateUnstartedWriter(DefaultOptions(batchSize: 1), TimeSpan.FromMinutes(10));

        // Act - 4 fill the channel, then 100 drops inside one warning window.
        for (int i = 0; i < 104; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });
        }

        // Assert
        LogRecords.Count(record => record.Level == LogLevel.Warning && record.Message.Contains("Dropped"))
                  .Should().Be(1, $"drops beyond the first must aggregate, not flood the log. Log:\n{LogDump()}");
    }

    /// <summary>
    /// The first drop past the interval logs a summary carrying everything dropped since the last
    /// warning - the aggregate is deferred, never lost.
    /// </summary>
    [Fact]
    public async Task TheNextDropAfterTheIntervalLogsASummaryWithTheAccumulatedCount()
    {
        // Arrange
        TelemetryWriter writer = CreateUnstartedWriter(DefaultOptions(batchSize: 1), TimeSpan.FromMilliseconds(100));

        // Act - 10 drops: the first logs immediately, 9 accumulate silently.
        for (int i = 0; i < 14; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });
        }

        await Task.Delay(150, TestContext.Current.CancellationToken);

        // The 11th drop arrives past the interval and must carry the 9 silent ones with it.
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        // Assert
        FakeLogRecord summary = LogRecords.Last(record => record.Level == LogLevel.Warning && record.Message.Contains("Dropped"));

        summary.StructuredState.Should().Contain(kv => kv.Key == "Count" && kv.Value == "10",
                                                 $"the summary must carry the 9 accumulated drops plus its own. Log:\n{LogDump()}");
        summary.StructuredState.Should().Contain(kv => kv.Key == "TotalDropped" && kv.Value == "11",
                                                 "the lifetime total rides along so a single log line orients an operator");
        LogRecords.Count(record => record.Level == LogLevel.Warning && record.Message.Contains("Dropped"))
                  .Should().Be(2, "one immediate first warning, one summary - nothing per-drop in between");
    }

    /// <summary>
    /// Every drop is counted in the health snapshot whatever the log throttling does - the log says
    /// "it is happening", the snapshot says exactly how much, and the alerting machinery watches the
    /// snapshot, not the log.
    /// </summary>
    [Fact]
    public async Task EveryDropIsCountedInTheHealthSnapshotRegardlessOfLogging()
    {
        // Arrange - seed first so the drain loop can flush once started; drops happen before start.
        await SeedPrinterAsync();
        TelemetryWriter writer =
            CreateUnstartedWriter(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05), TimeSpan.FromMinutes(10));

        for (int i = 0; i < 29; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });
        }

        // Act - start the drain loop so a health snapshot gets published.
        await writer.StartAsync(CancellationToken.None);

        bool counted = await WaitUntilAsync(
            () => Task.FromResult(writer.Current.DroppedMessages == 25),
            TimeSpan.FromSeconds(5));

        // Assert - capacity 4, so exactly 25 of the 29 were dropped, and only 1 was ever logged.
        counted.Should().BeTrue(
            $"expected the snapshot to report exactly 25 drops, saw {writer.Current.DroppedMessages}. Log:\n{LogDump()}");
        LogRecords.Count(record => record.Level == LogLevel.Warning && record.Message.Contains("Dropped"))
                  .Should().Be(1);
    }

    /// <summary>
    /// A stream of unprocessable messages logs one throttled Error, not one per message. A normal
    /// printer never sends these; an attacker can, at wire rate - and this is the heaviest log site
    /// in the class, a full stack trace per entry. <c>"UNKNOWN"</c> is the probe because it is both
    /// the attacker shape and a real possibility: the firmware's <c>to_str</c> default arm can
    /// genuinely emit it, while <c>ParseWireState</c> rejects it.
    /// </summary>
    [Fact]
    public async Task ABurstOfUnprocessableMessagesLogsOneErrorNotOnePerMessage()
    {
        // Arrange
        await SeedPrinterAsync();
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 500, flushIntervalSeconds: 0.05));

        // Act - five failures, then a good message behind them: once its sample lands, FIFO
        // guarantees all five failures were consumed (and logged or throttled) before we count.
        for (int i = 0; i < 5; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "UNKNOWN" });
        }

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool flushed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return await context.TelemetrySamples.CountAsync() == 1;
        }, TimeSpan.FromSeconds(5));

        // Assert
        flushed.Should().BeTrue($"the good message must land, proving the bad ones were consumed. Log:\n{LogDump()}");
        LogRecords.Count(record => record.Level == LogLevel.Error && record.Message.Contains("failed to process"))
                  .Should().Be(1, $"five failures inside one window must produce one Error, not five. Log:\n{LogDump()}");
    }

    /// <summary>
    /// Builds a writer without starting its drain loop, so the channel never empties and drop
    /// behaviour is deterministic: with batch size 1 the capacity is exactly 4, and every enqueue
    /// past that is a synchronous drop on the calling thread.
    /// </summary>
    private TelemetryWriter CreateUnstartedWriter(StorageOptions options, TimeSpan dropWarningInterval)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        _provider = services.BuildServiceProvider();

        _writer = new TelemetryWriter(_provider.GetRequiredService<IServiceScopeFactory>(),
                                      Options.Create(options),
                                      _fakeLogger,
                                      TimeProvider.System,
                                      new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance))
        {
            DropWarningInterval = dropWarningInterval,
        };

        return _writer;
    }

    /// <summary>
    /// A printer's live state must still persist correctly once whatever was blocking it resolves -
    /// not be stuck retrying a doomed <c>UPDATE</c> forever because an earlier, failed attempt is
    /// wrongly remembered as having already inserted the row.
    /// </summary>
    /// <remarks>
    /// This is the bug <see cref="TelemetryWriter"/>'s private <c>FlushAsync</c> defers its
    /// <c>ExistsInDatabase</c>/<c>ExistingSlotNumbers</c> update to prevent: marking either
    /// before <c>SaveChangesAsync</c> is confirmed to have succeeded would leave the cache believing
    /// a row exists the moment a save fails, so every later attempt chooses <c>Modified</c> over
    /// <c>Added</c> and issues an <c>UPDATE</c> against a row that was never created - permanently,
    /// for the rest of the process's life, even once the printer this test seeds partway through
    /// makes the underlying cause go away.
    /// </remarks>
    [Fact]
    public async Task APrinterStillPersistsAfterAnEarlierFlushFailedForIt()
    {
        // Arrange - no printer yet, so the first attempt(s) fail on the same foreign-key violation
        // PendingSamplesAreDiscardedRatherThanGrowingUnboundedWhileFlushesKeepFailing uses to force
        // a stuck database.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05));

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool firstAttemptFailed = await LoggedAsync(FlushFailed);

        firstAttemptFailed.Should().BeTrue("the arrangement depends on at least one flush having failed already");

        // Act - the underlying cause resolves; the row this printer needs now exists.
        await SeedPrinterAsync();

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "IDLE" });

        // Assert
        bool persisted = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            return state?.Status == PrinterStatus.Idle;
        }, TimeSpan.FromSeconds(5));

        persisted.Should().BeTrue("a printer must not be stuck forever just because its very first flush attempt failed");
    }

    /// <summary>
    /// A <c>Printer.LoadedMaterial</c> update must not survive a flush that otherwise failed - it
    /// has to be genuinely part of the same transaction as everything else, not a separate
    /// statement that already committed before the rest went wrong.
    /// </summary>
    /// <remarks>
    /// Forces a real partial failure rather than trusting the code change alone: printer 1 exists
    /// and is a perfectly valid target for the material update; printer 2 does not exist at all, so
    /// its <see cref="PrinterLiveState"/> insert violates the foreign key. Batch size 2 guarantees
    /// both land in the one <c>SaveChangesAsync</c> call this test needs to fail as a whole.
    /// </remarks>
    [Fact]
    public async Task LoadedMaterialRollsBackWithTheRestOfTheFlushOnFailure()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 2, flushIntervalSeconds: 30));
        await SeedPrinterAsync(printerId: 1);

        // Act - both land in the same flush; printer 2's missing row makes the whole batch fail.
        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING", Material = "PLA" });
        writer.Enqueue(printerId: 2, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool failed = await LoggedAsync(FlushFailed);

        failed.Should().BeTrue("the arrangement depends on printer 2's missing row making the batch fail");

        // Assert
        await using HomespoolDbContext verify = NewVerificationContext();
        Printer printer1 = await verify.Printers.SingleAsync(p => p.Id == 1, TestContext.Current.CancellationToken);
        printer1.LoadedMaterial.Should().BeNull("a failed flush must not leave a partial write behind");
    }

    /// <summary>
    /// The buffer safety net that guards against a different failure mode than the channel does:
    /// not a brief lag, but a database that keeps refusing every flush.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> seed a <see cref="Printer"/> row - every flush attempt then
    /// fails on the same foreign-key violation <c>TelemetryIsPersistedOnceTheBatchSizeIsReached</c>
    /// hit by accident while this suite was first being written, which makes "the database is stuck"
    /// trivial to force deterministically rather than needing to fake I/O failure.
    /// </remarks>
    [Fact]
    public async Task PendingSamplesAreDiscardedRatherThanGrowingUnboundedWhileFlushesKeepFailing()
    {
        // Arrange - batch size 1 and a short interval so many flush attempts happen quickly; no
        // printer exists, so every single one fails and SafeFlushAsync leaves the buffer as is.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05));

        // Act + Assert - feed until the cap (WriteBatchSize(1) * 20 = 20 here) is exceeded and the
        // trim fires.
        bool trimmed = await FeedUntilAsync(
            () => writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }),
            () => LogRecords.Any(record => record.Level == LogLevel.Warning
                                           && record.Message.Contains("Discarded")
                                           && record.StructuredState!.Any(kv => kv.Key == "Count")),
            TimeSpan.FromSeconds(30));

        trimmed.Should()
               .BeTrue($"the pending sample buffer must not grow without bound while every flush keeps failing. Log:\n{LogDump()}");
    }

    /// <summary>
    /// Events are capped too - far later than samples, but they are capped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Events are deliberately the last thing to give way: a sample is one frame of a dense stream
    /// and another arrives a second later, where an event happens once and is gone if dropped. But
    /// "last" was previously "never", and an unbounded buffer in a service that runs for months ends
    /// in the process dying - which loses every event it was protecting, plus the samples. A policy
    /// that sheds the oldest events loses strictly less than one that eventually loses all of them.
    /// </para>
    /// <para>
    /// Logged at Error rather than the sample trim's Warning: thinning history is degradation,
    /// discarding an event is data loss.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PendingEventsAreDiscardedRatherThanGrowingUnboundedWhileFlushesKeepFailing()
    {
        // Arrange - as above: no printer exists, so every flush fails and the buffer is left as is.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05));

        // Act + Assert - feed until the event cap (WriteBatchSize(1) * 10 = 10 here) is exceeded.
        bool trimmed = await FeedUntilAsync(
            () => writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new EventDTO { Status = "IDLE", EventType = Events.Info }),
            () => LogRecords.Any(record => record.Level == LogLevel.Error
                                           && record.Message.Contains("Discarded")
                                           && record.Message.Contains("event")
                                           && record.StructuredState!.Any(kv => kv.Key == "Count")),
            TimeSpan.FromSeconds(30));

        trimmed.Should().BeTrue($"the pending event buffer must have a ceiling too, even a distant one. Log:\n{LogDump()}");
    }
}
