using System;
using System.IO;
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
        services.AddDbContext<HSDbContext>(o => o.UseSqlite(_connectionString));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope migrationScope = _provider.CreateAsyncScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<HSDbContext>().Database.MigrateAsync();
        }

        _service = new TelemetryRetentionService(_provider.GetRequiredService<IServiceScopeFactory>(),
                                                  Options.Create(options),
                                                  NullLogger<TelemetryRetentionService>.Instance);

        await _service.StartAsync(CancellationToken.None);

        return _service;
    }

    private HSDbContext NewVerificationContext() =>
        new(new DbContextOptionsBuilder<HSDbContext>().UseSqlite(_connectionString).Options);

    private async Task SeedPrinterAsync(int printerId = 1)
    {
        await using HSDbContext context = NewVerificationContext();
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

    private async Task SeedSampleAsync(int printerId, DateTimeOffset timestamp)
    {
        await using HSDbContext context = NewVerificationContext();

        context.TelemetrySamples.Add(new TelemetrySample
        {
            PrinterId = printerId,
            Timestamp = timestamp,
            Status = PrinterStatus.Idle,
        });

        await context.SaveChangesAsync();
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
            await using HSDbContext context = NewVerificationContext();

            return await context.TelemetrySamples.CountAsync() == 1;
        }, TimeSpan.FromSeconds(5));

        sweptDown.Should().BeTrue("the sweep should run once at startup, without waiting for the hourly timer");

        await using HSDbContext verify = NewVerificationContext();
        TelemetrySample remaining = await verify.TelemetrySamples.SingleAsync();
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
        await Task.Delay(200);

        await using HSDbContext verify = NewVerificationContext();
        (await verify.TelemetrySamples.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CascadesToSlotSamples()
    {
        await SeedPrinterAsync();

        await using (HSDbContext context = NewVerificationContext())
        {
            context.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = 1,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
                Status = PrinterStatus.Idle,
                Slots = [new TelemetrySlotSample { SlotNumber = 1 }],
            });

            await context.SaveChangesAsync();
        }

        await StartServiceAsync(new StorageOptions { TelemetryRetentionDays = 14 });

        bool sweptDown = await WaitUntilAsync(async () =>
        {
            await using HSDbContext context = NewVerificationContext();

            return !await context.TelemetrySamples.AnyAsync();
        }, TimeSpan.FromSeconds(5));

        sweptDown.Should().BeTrue();

        await using HSDbContext verify = NewVerificationContext();
        (await verify.TelemetrySlotSamples.AnyAsync()).Should().BeFalse(
            "the FK to TelemetrySample is ON DELETE CASCADE and this database has foreign keys enabled");
    }
}
