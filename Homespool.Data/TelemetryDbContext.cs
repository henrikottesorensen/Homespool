using System;

using Microsoft.EntityFrameworkCore;

using Homespool.Model.Entities;

namespace Homespool.Data;

/// <summary>
/// The five tables the telemetry stream writes, addressed through a context of their own so they can
/// live either in the application database or in a private in-memory one that dies with the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which database this is depends on <see cref="StorageOptions.TelemetryInMemory"/>, and nothing
/// else in the application needs to know.</b> Off - the default - and it is the same SQLite file
/// <see cref="HomespoolDbContext"/> uses, writing the same rows into the same tables that the
/// migration created; on, and it is <c>Mode=Memory</c>, holding a bounded window that is never
/// written to disk. Every reader here goes through the same LINQ either way.
/// </para>
/// <para>
/// <b>Why a second context rather than a second provider on the existing one.</b> The application
/// database has to stay on disk regardless: accounts, printers, files, the queue and print history
/// are not telemetry and are not disposable. Only these five tables are, so only these five move.
/// </para>
/// <para>
/// <b>The relationships to <see cref="Printer"/> are deliberately not modelled here</b>, because in
/// memory there is no such table to point at, and one model for both modes is worth more than the
/// declaration. Nothing is lost against the file: those foreign keys exist in the schema the
/// migration created and SQLite goes on enforcing them, which is what the writer's insert failures
/// have always actually come from. Against a memory database they are simply absent, and the
/// deletions they used to cascade are done explicitly instead - see <c>ITelemetryEviction</c>.
/// </para>
/// <para>
/// <b>An in-memory database exists only while a connection to it is open</b>, so one is held for the
/// lifetime of the process; see <see cref="DataServiceCollectionExtensions.AddHomespoolData"/>. The
/// tables are created there at startup rather than migrated - there is no history to carry forward
/// in a database that begins empty every time.
/// </para>
/// </remarks>
public class TelemetryDbContext : DbContext
{
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options)
        : base(options)
    {
    }

    /// <summary>Last-known state, one row per printer. Upserted; never grows.</summary>
    public DbSet<PrinterLiveState> PrinterLiveStates { get; set; }

    /// <summary>Last-known per-slot state. Empty for single-tool printers.</summary>
    public DbSet<PrinterLiveSlotState> PrinterLiveSlotStates { get; set; }

    /// <summary>Append-only dense telemetry history. Subject to retention.</summary>
    public DbSet<TelemetrySample> TelemetrySamples { get; set; }

    /// <summary>Per-slot telemetry history. Swept by cascade when its parent sample is deleted.</summary>
    public DbSet<TelemetrySlotSample> TelemetrySlotSamples { get; set; }

    /// <summary>Discrete printer events. Swept by age and by a per-printer row cap.</summary>
    public DbSet<PrinterEvent> PrinterEvents { get; set; }

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as epoch milliseconds in an INTEGER column, exactly
    /// as <see cref="HomespoolDbContext"/> does.
    /// </summary>
    /// <remarks>
    /// <b>This has to match, not merely resemble.</b> In file mode the two contexts read and write the
    /// same columns, so a different representation here would write timestamps the rest of the
    /// application cannot read - and the bucketed temperature query depends on the integer form
    /// specifically, since it groups by integer division on the raw column.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>()
                            .HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        TelemetryModel.Configure(modelBuilder, withPrinterForeignKeys: false);
    }
}
