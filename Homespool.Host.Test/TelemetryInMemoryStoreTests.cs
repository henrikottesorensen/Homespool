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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="StorageOptions.TelemetryInMemory"/> - telemetry diverted to a database that never
/// touches the disk, so an SD card takes no writes for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against a real in-memory SQLite, not a substitute or the EF in-memory provider</b>, because
/// every property worth testing here is a property of that database: that separate connections see
/// one store, that it evaporates without a connection held open, and that the application file is
/// genuinely left alone.
/// </para>
/// <para>
/// The application database is still a real file, as it is in production - only the five telemetry
/// tables move.
/// </para>
/// </remarks>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                 Justification = "Verification contexts are short-lived readers over databases this class owns and "
                                 + "tears down in Dispose; EF opens and closes their connections per query.")]
public sealed class TelemetryInMemoryStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-telememory-{Guid.NewGuid():N}.db");

    private readonly string _memoryConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = $"HomespoolTelemetryTest-{Guid.NewGuid():N}",
        Mode = SqliteOpenMode.Memory,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
    }.ToString();

    private TelemetryKeepAlive? _keepAlive;
    private ServiceProvider? _provider;
    private TelemetryWriter? _writer;

    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification = "IDisposable.Dispose cannot be asynchronous, and the writer must be stopped "
                                     + "before the databases it holds are torn down.")]
    public void Dispose()
    {
        if (_writer is not null)
        {
            _writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _writer.Dispose();
        }

        _provider?.Dispose();
        _keepAlive?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// <b>The property the whole design rests on: separate connections see one database.</b> Every
    /// context here is short-lived and pooled, so if each got a private store the writer would be
    /// filling one nobody ever reads.
    /// </summary>
    [Fact]
    public async Task SeparateContextsShareOneInMemoryStore()
    {
        // Arrange
        _keepAlive = new TelemetryKeepAlive(_memoryConnectionString);

        await using (TelemetryDbContext creator = TestTelemetryContext.ForConnectionString(_memoryConnectionString))
        {
            await creator.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        // Act - written through one context...
        await using (TelemetryDbContext writer = TestTelemetryContext.ForConnectionString(_memoryConnectionString))
        {
            writer.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = 1,
                Timestamp = DateTimeOffset.UtcNow,
                Status = PrinterStatus.Printing,
            });

            await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert - ...and read back through another
        await using TelemetryDbContext reader = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

        (await reader.TelemetrySamples.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1, "a shared-cache in-memory database is one store, not one per connection");
    }

    /// <summary>
    /// <b>Row ids stay monotonic across connections</b>, which is not a detail:
    /// <c>QueueAdvancer</c> learns of file arrivals by walking <see cref="PrinterEvent.Id"/> forward
    /// from a watermark, so an id that restarted or repeated would make it re-read or skip arrivals.
    /// </summary>
    [Fact]
    public async Task EventIdsKeepAscendingAcrossConnections()
    {
        // Arrange
        _keepAlive = new TelemetryKeepAlive(_memoryConnectionString);

        await using (TelemetryDbContext creator = TestTelemetryContext.ForConnectionString(_memoryConnectionString))
        {
            await creator.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        // Act - one event per connection, three connections
        for (int i = 0; i < 3; i++)
        {
            await using TelemetryDbContext context = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

            context.PrinterEvents.Add(new PrinterEvent
            {
                PrinterId = 1,
                Timestamp = DateTimeOffset.UtcNow,
                EventType = PrinterEventType.StateChanged,
                WireType = "STATE_CHANGED",
                Status = PrinterStatus.Idle,
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using TelemetryDbContext verify = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

        (await verify.PrinterEvents.Select(e => e.Id).OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken))
            .Should().Equal([1, 2, 3], "the queue's arrival watermark walks these forward and cannot tolerate reuse");
    }

    /// <summary>
    /// <b>Why <see cref="TelemetryKeepAlive"/> exists.</b> Without a connection held open the database
    /// does not empty - it ceases to exist, tables and all.
    /// </summary>
    /// <remarks>
    /// <b>And it goes sooner than "when the context is disposed".</b> EF opens and closes a connection
    /// per operation, so the tables created here are already gone by the next statement against the
    /// very same context - which is what makes the keepalive a requirement rather than a tidy-up, and
    /// why it is constructed eagerly in <c>AddHomespoolData</c> rather than resolved lazily.
    /// </remarks>
    [Fact]
    public async Task WithoutAKeepAliveTheStoreDoesNotSurvive()
    {
        // Arrange - tables created, with nothing holding the database open afterwards
        await using TelemetryDbContext context = TestTelemetryContext.ForConnectionString(_memoryConnectionString);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // Act - an async lambda, so a provider that throws while building the query still surfaces it
        // as a faulted task rather than synchronously, past the assertion.
        Func<Task> read = async () => await context.TelemetrySamples.CountAsync(TestContext.Current.CancellationToken);

        // Assert
        (await read.Should().ThrowAsync<SqliteException>(
             "the last connection closing takes the whole database with it, which is what the keepalive prevents"))
            .WithMessage("*no such table*");
    }

    /// <summary>
    /// <b>The point of the setting: nothing about telemetry reaches the disk.</b> Samples are 98% of
    /// what the application database holds, so this is the whole of the write saving.
    /// </summary>
    [Fact]
    public async Task TelemetryIsPersistedToMemoryAndNotToTheApplicationDatabase()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync();

        // Act
        writer.Enqueue(1, DateTimeOffset.UtcNow, PrusaTelemetryMapping.ToUpdate(new TelemetryDTO { Status = "PRINTING" }));

        bool stored = await WaitUntilAsync(async () =>
        {
            await using TelemetryDbContext telemetry = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

            return await telemetry.TelemetrySamples.AnyAsync(TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        // Assert
        stored.Should().BeTrue("the writer must still persist - to the memory store");

        await using HomespoolDbContext file = NewApplicationContext();

        (await file.TelemetrySamples.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "no sample may reach the file; that is the entire purpose of the setting");
    }

    /// <summary>
    /// <b>Last-known state survives a planned restart, and only it.</b> A printer that is switched off
    /// has nothing to refill its state, so discarding it would leave a machine the application knew
    /// about reading as one that has never connected - one of the two printers on the appliance this
    /// was measured against had been off for six days.
    /// </summary>
    [Fact]
    public async Task LastKnownStateIsWrittenToTheFileAtShutdown()
    {
        // Arrange
        TelemetryWriter writer = await StartWriterAsync();

        writer.Enqueue(1, DateTimeOffset.UtcNow,
                       PrusaTelemetryMapping.ToUpdate(new TelemetryDTO { Status = "PRINTING", Progress = 42 }));

        bool stored = await WaitUntilAsync(async () =>
        {
            await using TelemetryDbContext telemetry = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

            return await telemetry.PrinterLiveStates.AnyAsync(TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(5));

        stored.Should().BeTrue("the arrangement depends on live state having reached the memory store first");

        // Act - a planned stop, which is what a restart or an upgrade does
        await writer.StopAsync(CancellationToken.None);
        _writer = null;
        writer.Dispose();

        // Assert
        await using HomespoolDbContext file = NewApplicationContext();

        PrinterLiveState? persisted = await file.PrinterLiveStates
                                                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull("a restart must not forget what a printer last reported");
        persisted!.Status.Should().Be(PrinterStatus.Printing);
        persisted.Progress.Should().Be(42);

        (await file.TelemetrySamples.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "history is still not written - one row per printer is, and nothing else");
    }

    /// <summary>
    /// <b>The writer starts from what was saved, rather than treating a restart as a first sighting.</b>
    /// Hydrating from the application database is the other half of the shutdown writeback: without
    /// it the row would be read back by nothing and a printer would still appear to have never
    /// connected.
    /// </summary>
    [Fact]
    public async Task AWriterStartingUpHydratesLastKnownStateFromTheFile()
    {
        // Arrange - a previous process's shutdown writeback, already on disk
        await SeedPrinterAsync();

        await using (HomespoolDbContext seed = NewApplicationContext())
        {
            seed.PrinterLiveStates.Add(new PrinterLiveState
            {
                PrinterId = 1,
                Status = PrinterStatus.Printing,
                Material = "PETG",
                LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1),
            });

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        TelemetryWriter writer = await StartWriterAsync(seedPrinter: false);

        // Act - a slim message, carrying no material, so only a hydrated state can still know it
        writer.Enqueue(1, DateTimeOffset.UtcNow, PrusaTelemetryMapping.ToUpdate(new TelemetryDTO { Status = "IDLE" }));

        // Assert
        bool merged = await WaitUntilAsync(async () =>
        {
            await using TelemetryDbContext telemetry = TestTelemetryContext.ForConnectionString(_memoryConnectionString);

            PrinterLiveState? state = await telemetry.PrinterLiveStates
                                                     .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            return state is { Status: PrinterStatus.Idle, Material: "PETG" };
        }, TimeSpan.FromSeconds(5));

        merged.Should().BeTrue("the material was never re-sent, so it can only have come from the file");
    }

    /// <summary>
    /// <b>The startup path itself, which nothing else here exercises.</b> Registration decides which
    /// database the telemetry context opens against, creates the tables when that is a memory one, and
    /// holds it in existence - three things that only fail together, at boot, on a deployment that has
    /// just turned the setting on.
    /// </summary>
    [Fact]
    public void RegistrationWiresAWorkingMemoryStoreAndLeavesTheFileWithoutTelemetry()
    {
        // Arrange - configuration as an operator would write it
        IConfiguration configuration = new ConfigurationBuilder()
                                       .AddInMemoryCollection(new Dictionary<string, string?>
                                       {
                                           ["ConnectionStrings:HomespoolDb"] = $"Data Source={_databasePath}",
                                           ["Storage:TelemetryInMemory"] = "true",
                                       })
                                       .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddHomespoolData(configuration);

        // Act
        _provider = services.BuildServiceProvider();
        _provider.MigrateHomespoolData();

        // Assert - the telemetry context is usable, and is not the application database
        using IServiceScope scope = _provider.CreateScope();

        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        HomespoolDbContext application = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        telemetry.Database.GetConnectionString()
                 .Should().Contain("Mode=Memory", "the setting is what diverts telemetry off the disk");

        telemetry.Database.GetConnectionString()
                 .Should().NotBe(application.Database.GetConnectionString(),
                                 "the two contexts must not be pointed at one database when this is on");

        // The tables exist, which is EnsureTelemetryStore's whole job - a read that does not throw is
        // the assertion, since a memory database that was never created has no tables at all.
        telemetry.TelemetrySamples.Count().Should().Be(0);
        telemetry.PrinterLiveStates.Count().Should().Be(0);
    }

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

    private HomespoolDbContext NewApplicationContext()
    {
        return new(new DbContextOptionsBuilder<HomespoolDbContext>()
                   .UseSqlite($"Data Source={_databasePath}")
                   .Options);
    }

    private async Task SeedPrinterAsync()
    {
        await using HomespoolDbContext context = NewApplicationContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(new Printer
        {
            Id = 1,
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
    /// A writer wired the way <c>AddHomespoolData</c> wires one with the setting on: the application
    /// database on a file, the telemetry context on a shared in-memory database, and a keepalive
    /// holding the latter in existence.
    /// </summary>
    private async Task<TelemetryWriter> StartWriterAsync(bool seedPrinter = true)
    {
        if (seedPrinter)
        {
            await SeedPrinterAsync();
        }

        _keepAlive = new TelemetryKeepAlive(_memoryConnectionString);

        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));
        services.AddDbContext<TelemetryDbContext>(o => o.UseSqlite(_memoryConnectionString));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TelemetryDbContext>()
                       .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        _writer = new TelemetryWriter(_provider.GetRequiredService<IServiceScopeFactory>(),
                                      TestOptions.Monitor(new StorageOptions
                                      {
                                          TelemetryInMemory = true,
                                          WriteBatchSize = 1,
                                          WriteFlushIntervalSeconds = 0.05,
                                      }),
                                      NullLogger<TelemetryWriter>.Instance,
                                      TimeProvider.System);

        await _writer.StartAsync(CancellationToken.None);

        return _writer;
    }
}
