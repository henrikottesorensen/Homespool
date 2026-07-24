using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

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
    private ServiceProvider? _provider;
    private TelemetryWriter? _writer;
    // A capturing rather than null logger: a flush failure is caught and logged rather than crashing
    // the writer (see TelemetryWriter.SafeFlushAsync), and a real logger here is what makes that
    // visible while debugging a test failure instead of silently swallowed.
    private readonly CapturingLogger<TelemetryWriter> _capturingLogger = new();

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
    private async Task<TelemetryWriter> StartWriterAsync(StorageOptions options)
    {
        ServiceCollection services = new();
        services.AddDbContext<PSDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope migrationScope = _provider.CreateAsyncScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<PSDbContext>().Database.MigrateAsync();
        }

        _writer = new TelemetryWriter(_provider.GetRequiredService<IServiceScopeFactory>(),
                                       Options.Create(options),
                                       _capturingLogger);

        await _writer.StartAsync(CancellationToken.None);

        return _writer;
    }

    private PSDbContext NewVerificationContext() =>
        new(new DbContextOptionsBuilder<PSDbContext>().UseSqlite($"Data Source={_databasePath}").Options);

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

    private static async Task<int> SampleCountAsync(PSDbContext context) =>
        await context.TelemetrySamples.CountAsync();

    private static StorageOptions DefaultOptions(int batchSize = 500, double flushIntervalSeconds = 30, double throttleSeconds = 0) =>
        new()
        {
            WriteBatchSize = batchSize,
            WriteFlushIntervalSeconds = flushIntervalSeconds,
            MinimumSampleIntervalSeconds = throttleSeconds,
        };

    /// <summary>
    /// <see cref="PrinterLiveState"/> and <see cref="TelemetrySample"/> both carry a required FK to
    /// <see cref="Printer"/>, enforced by SQLite - the writer only ever sees a <c>printerId</c> the
    /// auth handler already resolved against a real enrolled printer, so every test needs one to
    /// exist before enqueuing anything.
    /// </summary>
    private async Task SeedPrinterAsync(int printerId = 1)
    {
        await using PSDbContext context = NewVerificationContext();
        await context.Database.MigrateAsync();

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync();

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
        await context.SaveChangesAsync();
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
            await using PSDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue("a single item should flush immediately once it reaches the batch size");

        await using PSDbContext verify = NewVerificationContext();
        PrinterLiveState state = await verify.PrinterLiveStates.SingleAsync();
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
            await using PSDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue("the periodic timer must flush a low-traffic printer even though its batch never fills");
    }

    [Fact]
    public async Task TheThrottleSkipsTheSampleButTheLiveStateStillMerges()
    {
        // Arrange - a throttle window comfortably longer than the two messages are apart. The flush
        // interval is short (unlike the batch-size test above): a throttled message never adds to
        // pendingSamples, so its dirty live-state can only reach the database via the timer, not the
        // batch-size trigger.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.1, throttleSeconds: 3600));
        await SeedPrinterAsync();

        DateTimeOffset first = DateTimeOffset.UtcNow;

        // Act
        writer.Enqueue(printerId: 1, first, new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 200 });

        bool firstFlushed = await WaitUntilAsync(async () =>
        {
            await using PSDbContext context = NewVerificationContext();
            return await SampleCountAsync(context) == 1;
        }, TimeSpan.FromSeconds(5));

        firstFlushed.Should().BeTrue();

        writer.Enqueue(printerId: 1, first.AddSeconds(1), new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 210 });

        bool liveStateUpdated = await WaitUntilAsync(async () =>
        {
            await using PSDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync();
            return state?.NozzleTemperature == 210;
        }, TimeSpan.FromSeconds(5));

        // Assert
        liveStateUpdated.Should().BeTrue("the live view must reflect the newest message regardless of the throttle");

        await using PSDbContext verify = NewVerificationContext();
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
            await using PSDbContext context = NewVerificationContext();
            return await context.PrinterEvents.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        flushed.Should().BeTrue();

        await using PSDbContext verify = NewVerificationContext();
        PrinterEvent stored = await verify.PrinterEvents.SingleAsync();

        stored.PrinterId.Should().Be(1);
        stored.EventType.Should().Be(Events.Info);
        stored.Status.Should().Be(PrinterStatus.Idle);
        stored.JobId.Should().Be(42);
        stored.Payload.Should().Be("""{"firmware":"6.4.0"}""");
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
            await using PSDbContext context = NewVerificationContext();
            return await context.PrinterLiveStates.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool secondFlushLanded = await WaitUntilAsync(async () =>
        {
            await using PSDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync();
            return state?.Status == PrinterStatus.Printing;
        }, TimeSpan.FromSeconds(5));

        // Assert
        secondFlushLanded.Should().BeTrue();

        await using PSDbContext verify = NewVerificationContext();
        (await verify.PrinterLiveStates.CountAsync()).Should().Be(1,
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
            await using PSDbContext context = NewVerificationContext();
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
            await using PSDbContext context = NewVerificationContext();
            return await context.PrinterLiveSlotStates.CountAsync() == 2;
        }, TimeSpan.FromSeconds(5));

        // Assert
        secondSlotPersisted.Should().BeTrue("a slot seen for the first time in a later flush must be inserted, not silently dropped");

        await using PSDbContext verify = NewVerificationContext();
        (await verify.PrinterLiveSlotStates.Select(s => s.SlotNumber).OrderBy(n => n).ToListAsync())
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
            await using PSDbContext context = NewVerificationContext();
            Printer printer = await context.Printers.SingleAsync();
            return printer.LoadedMaterial == "PLA";
        }, TimeSpan.FromSeconds(5));

        populated.Should().BeTrue();
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
        Task enqueueTask = Task.Run(enqueueMany);
        Task completed = await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(5)));

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
        bool warned = await WaitUntilAsync(
            () => Task.FromResult(_capturingLogger.Lines.Any(line => line.Contains("Warning") && line.Contains("Dropped"))),
            TimeSpan.FromSeconds(5));

        warned.Should().BeTrue("a full channel under DropOldest must be logged, not silently discarded");
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
    /// <summary>
    /// A printer's live state must still persist correctly once whatever was blocking it resolves -
    /// not be stuck retrying a doomed <c>UPDATE</c> forever because an earlier, failed attempt is
    /// wrongly remembered as having already inserted the row.
    /// </summary>
    /// <remarks>
    /// This is the bug <see cref="TelemetryWriter.FlushAsync"/>'s deferred
    /// <c>ExistsInDatabase</c>/<c>ExistingSlotNumbers</c> update exists to prevent: marking either
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
        // as the test above.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05));

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

        bool firstAttemptFailed = await WaitUntilAsync(
            () => Task.FromResult(_capturingLogger.Lines.Any(line => line.Contains("Telemetry flush failed"))),
            TimeSpan.FromSeconds(5));

        firstAttemptFailed.Should().BeTrue("the arrangement depends on at least one flush having failed already");

        // Act - the underlying cause resolves; the row this printer needs now exists.
        await SeedPrinterAsync();

        writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "IDLE" });

        // Assert
        bool persisted = await WaitUntilAsync(async () =>
        {
            await using PSDbContext context = NewVerificationContext();
            PrinterLiveState? state = await context.PrinterLiveStates.SingleOrDefaultAsync();
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

        bool failed = await WaitUntilAsync(
            () => Task.FromResult(_capturingLogger.Lines.Any(line => line.Contains("Telemetry flush failed"))),
            TimeSpan.FromSeconds(5));

        failed.Should().BeTrue("the arrangement depends on printer 2's missing row making the batch fail");

        // Assert
        await using PSDbContext verify = NewVerificationContext();
        Printer printer1 = await verify.Printers.SingleAsync(p => p.Id == 1);
        printer1.LoadedMaterial.Should().BeNull("a failed flush must not leave a partial write behind");
    }

    [Fact]
    public async Task PendingSamplesAreDiscardedRatherThanGrowingUnboundedWhileFlushesKeepFailing()
    {
        // Arrange - batch size 1 and a short interval so many flush attempts happen quickly; no
        // printer exists, so every single one fails and SafeFlushAsync leaves the buffer as is.
        TelemetryWriter writer = await StartWriterAsync(DefaultOptions(batchSize: 1, flushIntervalSeconds: 0.05));

        // Act - comfortably past the cap (WriteBatchSize(1) * 20 = 20 here). Yielding periodically
        // matters: batch size 1 also makes the channel's own capacity tiny (1 * CapacityBatches),
        // so a single tight synchronous burst would mostly self-inflict DropOldest against the
        // channel before the reader is ever scheduled to drain it into the buffer this test is
        // actually targeting - which is a different safety net (see the class remarks).
        for (int i = 0; i < 500; i++)
        {
            writer.Enqueue(printerId: 1, DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" });

            if (i % 10 == 0)
            {
                await Task.Delay(1);
            }
        }

        // Assert
        bool trimmed = await WaitUntilAsync(
            () => Task.FromResult(_capturingLogger.Lines.Any(line => line.Contains("Discarded") && line.Contains("buffered telemetry samples"))),
            TimeSpan.FromSeconds(5));

        trimmed.Should().BeTrue($"the pending sample buffer must not grow without bound while every flush keeps failing. Logs:\n{string.Join('\n', _capturingLogger.Lines)}");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Enqueue($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" -- {exception}")}");
        }
    }
}
