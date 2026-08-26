using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TelemetryRetentionService"/> - the hourly sweep that deletes
/// <see cref="TelemetrySample"/> rows past <see cref="StorageOptions.TelemetryRetentionDays"/>.
/// </summary>
/// <remarks>
/// Run against real SQLite, matching <c>TelemetryWriterTests</c> - the sweep is a bulk
/// <c>ExecuteDeleteAsync</c>, which an in-memory provider does not execute the same way a real
/// database does.
/// </remarks>
public sealed class TelemetryRetentionServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-retention-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private ServiceProvider? _provider;
    private TelemetryRetentionService? _service;

    public TelemetryRetentionServiceTests()
    {
        // Mirrors DataServiceCollectionExtensions.AddHomespoolData: foreign keys are a
        // connection-string keyword, and the cascade from TelemetrySample to TelemetrySlotSample this
        // service relies on only fires with them enabled - a bare "Data Source=..." string leaves
        // SQLite's default (off) in place and orphans would go unnoticed.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>
    /// Polls rather than sleeping a fixed duration - the sweep runs on its own task, so there is no
    /// single moment a test can await to know it has run.
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

    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "IDisposable.Dispose cannot be asynchronous, and the service must be stopped before the test's resources are torn down.")]
    public void Dispose()
    {
        if (_service is not null)
        {
            _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _service.Dispose();
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

    private async Task<TelemetryRetentionService> StartServiceAsync(StorageOptions options)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(o => o.UseSqlite(_connectionString));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope migrationScope = _provider.CreateAsyncScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<HomespoolDbContext>().Database
                                .MigrateAsync(TestContext.Current.CancellationToken);
        }

        _service = new TelemetryRetentionService(_provider.GetRequiredService<IServiceScopeFactory>(),
                                                 Options.Create(options),
                                                 NullLogger<TelemetryRetentionService>.Instance);

        await _service.StartAsync(CancellationToken.None);

        return _service;
    }

    private HomespoolDbContext NewVerificationContext()
    {
        return new(new DbContextOptionsBuilder<HomespoolDbContext>().UseSqlite(_connectionString).Options);
    }

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
    /// One event, at a chosen time. Ids come from the table's own sequence, so the order events are
    /// seeded in is the order the cap will trim them in.
    /// </summary>
    private async Task SeedEventAsync(int printerId, DateTimeOffset timestamp)
    {
        await using HomespoolDbContext context = NewVerificationContext();

        context.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = printerId,
            Timestamp = timestamp,
            EventType = PrinterEventType.StateChanged,
            WireType = "STATE_CHANGED",
            Status = PrinterStatus.Idle,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedSampleAsync(int printerId, DateTimeOffset timestamp)
    {
        await using HomespoolDbContext context = NewVerificationContext();

        context.TelemetrySamples.Add(new TelemetrySample
        {
            PrinterId = printerId,
            Timestamp = timestamp,
            Status = PrinterStatus.Idle,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartupSweepDeletesSamplesPastRetentionAndKeepsRecentOnes()
    {
        await SeedPrinterAsync();
        await SeedSampleAsync(1, DateTimeOffset.UtcNow.AddDays(-100));
        await SeedSampleAsync(1, DateTimeOffset.UtcNow.AddHours(-1));

        await StartServiceAsync(new StorageOptions { TelemetryRetentionDays = 14 });

        bool sweptDown = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return await context.TelemetrySamples.CountAsync() == 1;
        }, TimeSpan.FromSeconds(5));

        sweptDown.Should().BeTrue("the sweep should run once at startup, without waiting for the hourly timer");

        await using HomespoolDbContext verify = NewVerificationContext();
        TelemetrySample remaining = await verify.TelemetrySamples.SingleAsync(TestContext.Current.CancellationToken);
        remaining.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RetentionDaysZeroDisablesTheSweep()
    {
        await SeedPrinterAsync();
        await SeedSampleAsync(1, DateTimeOffset.UtcNow.AddDays(-1000));

        await StartServiceAsync(new StorageOptions { TelemetryRetentionDays = 0 });

        // Nothing to poll for a negative - give the (disabled) sweep a beat to have run, then assert
        // the row is still there.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.TelemetrySamples.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task CascadesToSlotSamples()
    {
        await SeedPrinterAsync();

        await using (HomespoolDbContext context = NewVerificationContext())
        {
            context.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = 1,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
                Status = PrinterStatus.Idle,
                Slots = [new TelemetrySlotSample { SlotNumber = 1 }],
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await StartServiceAsync(new StorageOptions { TelemetryRetentionDays = 14 });

        bool sweptDown = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return !await context.TelemetrySamples.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        sweptDown.Should().BeTrue();

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.TelemetrySlotSamples.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse(
            "the FK to TelemetrySample is ON DELETE CASCADE and this database has foreign keys enabled");
    }

    /// <summary>
    /// Events past <see cref="StorageOptions.EventRetentionDays"/> go, and events inside it stay.
    /// Both halves, because a sweep that deleted everything would satisfy the first alone.
    /// </summary>
    [Fact]
    public async Task TheAgeSweepDeletesOldEventsAndKeepsRecentOnes()
    {
        // Arrange
        await SeedPrinterAsync();
        await SeedEventAsync(1, DateTimeOffset.UtcNow.AddDays(-40));
        await SeedEventAsync(1, DateTimeOffset.UtcNow.AddDays(-1));

        // Act
        await StartServiceAsync(new StorageOptions
        {
            TelemetryRetentionDays = 0,
            EventRetentionDays = 30,
            MaxEventsPerPrinter = 0,
        });

        // Assert
        bool swept = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return await context.PrinterEvents.CountAsync(TestContext.Current.CancellationToken) == 1;
        }, TimeSpan.FromSeconds(10));

        swept.Should().BeTrue("the 40-day-old event is past a 30-day window");

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.PrinterEvents.SingleAsync(TestContext.Current.CancellationToken))
            .Timestamp.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(-2), "the recent one survives");
    }

    /// <summary>
    /// Zero means off, the same way it does for samples - so a deployment that wants events forever
    /// says so rather than discovering a default.
    /// </summary>
    [Fact]
    public async Task ZeroEventRetentionDaysSweepsNothingByAge()
    {
        // Arrange
        await SeedPrinterAsync();
        await SeedEventAsync(1, DateTimeOffset.UtcNow.AddYears(-5));

        // Act
        await StartServiceAsync(new StorageOptions
        {
            TelemetryRetentionDays = 0,
            EventRetentionDays = 0,
            MaxEventsPerPrinter = 0,
        });

        // Assert - poll for the opposite, so the assertion is not just "it has not run yet"
        bool swept = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return !await context.PrinterEvents.AnyAsync(TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(2));

        swept.Should().BeFalse("a five-year-old event survives a disabled sweep");
    }

    /// <summary>
    /// <b>The cap keeps the newest and drops the rest.</b> Age cannot do this: a printer emitting at
    /// the transport's ceiling fills a disk long inside any window an operator would choose.
    /// </summary>
    [Fact]
    public async Task TheCountCapKeepsTheNewestEventsAndDropsTheOldest()
    {
        // Arrange - ten events, all recent, so only the cap can remove any
        await SeedPrinterAsync();

        for (int i = 0; i < 10; i++)
        {
            await SeedEventAsync(1, DateTimeOffset.UtcNow.AddMinutes(-i));
        }

        // Act
        await StartServiceAsync(new StorageOptions
        {
            TelemetryRetentionDays = 0,
            EventRetentionDays = 0,
            MaxEventsPerPrinter = 4,
        });

        // Assert
        bool trimmed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return await context.PrinterEvents.CountAsync(TestContext.Current.CancellationToken) == 4;
        }, TimeSpan.FromSeconds(10));

        trimmed.Should().BeTrue("ten rows trimmed to a cap of four");

        await using HomespoolDbContext verify = NewVerificationContext();
        List<long> kept = await verify.PrinterEvents.Select(e => e.Id)
                                      .OrderBy(id => id)
                                      .ToListAsync(TestContext.Current.CancellationToken);

        kept.Should().Equal([7, 8, 9, 10], "the newest four by id, not an arbitrary four");
    }

    /// <summary>
    /// <b>The cap is per printer, and this is the half that matters.</b> A global cap would let one
    /// chatty printer evict everybody else's events - the failure <c>internet-exposure.md</c> names
    /// for a global rate limiter, in the one place partitioning is actually available.
    /// </summary>
    [Fact]
    public async Task TheCountCapDoesNotLetOnePrintersFloodEvictAnothers()
    {
        // Arrange
        await SeedPrinterAsync(1);
        await SeedPrinterAsync(2);

        for (int i = 0; i < 10; i++)
        {
            await SeedEventAsync(1, DateTimeOffset.UtcNow.AddMinutes(-i));
        }

        await SeedEventAsync(2, DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedEventAsync(2, DateTimeOffset.UtcNow);

        // Act
        await StartServiceAsync(new StorageOptions
        {
            TelemetryRetentionDays = 0,
            EventRetentionDays = 0,
            MaxEventsPerPrinter = 4,
        });

        // Assert
        bool trimmed = await WaitUntilAsync(async () =>
        {
            await using HomespoolDbContext context = NewVerificationContext();

            return await context.PrinterEvents.CountAsync(e => e.PrinterId == 1,
                                                          TestContext.Current.CancellationToken) == 4;
        }, TimeSpan.FromSeconds(10));

        trimmed.Should().BeTrue("the flooding printer is trimmed to the cap");

        await using HomespoolDbContext verify = NewVerificationContext();
        (await verify.PrinterEvents.CountAsync(e => e.PrinterId == 2, TestContext.Current.CancellationToken))
            .Should().Be(2, "the quiet printer keeps everything it had");
    }
}
