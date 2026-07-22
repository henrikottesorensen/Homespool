using System;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using PrinterService.Model.Entities;

namespace PrinterService.Data;

/// <summary>
/// The application database context.
/// </summary>
/// <remarks>
/// Derives from <see cref="IdentityDbContext{TUser,TRole,TKey}"/> rather than plain
/// <c>DbContext</c>. <c>Program.cs</c> calls <c>AddEntityFrameworkStores&lt;PSDbContext&gt;()</c>,
/// which requires the context to expose the Identity entity sets — with a plain
/// <c>DbContext</c> the Identity tables are never created and user store resolution fails at
/// startup. The key type is <see cref="long"/> to match <see cref="PSUser"/>.
/// </remarks>
public class PSDbContext : IdentityDbContext<PSUser, IdentityRole<long>, long>, IDataProtectionKeyContext
{
    public DbSet<Printer> Printers { get; set; }

    public DbSet<PrusaConnectAuthenticationData> PrusaConnectAuthentication { get; set; }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    /// <summary>Last-known state, one row per printer. Upserted; never grows.</summary>
    public DbSet<PrinterLiveState> PrinterLiveStates { get; set; }

    /// <summary>Append-only dense telemetry history. Subject to retention.</summary>
    public DbSet<TelemetrySample> TelemetrySamples { get; set; }

    /// <summary>Discrete printer events. Retained indefinitely.</summary>
    public DbSet<PrinterEvent> PrinterEvents { get; set; }

    /// <summary>Last-known per-slot state. Empty for single-tool printers.</summary>
    public DbSet<PrinterLiveSlotState> PrinterLiveSlotStates { get; set; }

    /// <summary>Per-slot telemetry history. Swept by cascade when its parent sample is deleted.</summary>
    public DbSet<TelemetrySlotSample> TelemetrySlotSamples { get; set; }

    public PSDbContext(DbContextOptions<PSDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as epoch milliseconds in an INTEGER column.
    /// </summary>
    /// <remarks>
    /// Applied as a convention rather than property by property, so a timestamp added later cannot
    /// silently fall back to the untranslatable TEXT mapping. See
    /// <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> for why the default mapping — and EF's
    /// own <c>DateTimeOffsetToBinaryConverter</c> — are both unusable here. This also covers
    /// Identity's own <c>LockoutEnd</c>.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>()
                            .HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Printer>(entity =>
        {
            // The public identifier used in URLs, so it is looked up on every such request and
            // must be unique. Deliberately not the primary key — see Printer.Id.
            entity.HasIndex(e => e.Uuid)
                  .IsUnique();

            // Listing printers is always scoped to their owner.
            entity.HasIndex(e => e.Owner);
        });

        builder.Entity<PrusaConnectAuthenticationData>(entity =>
        {
            // A printer is identified on the wire by its fingerprint, on every request and on
            // the WebSocket upgrade (AGENT-NOTES §9). That lookup is the hot path for every
            // connection, and two rows sharing a fingerprint would make identity ambiguous.
            entity.HasIndex(e => e.FingerPrint)
                  .IsUnique();

            // Deliberately NOT unique. The fingerprint is SHA-256 of
            // (STM32 CPU UUID || factory MAC || serial number), so it already subsumes the serial:
            // two different serials cannot produce one fingerprint, and uniqueness here protects
            // nothing that the fingerprint index does not already cover.
            //
            // It did cause harm. A mainboard replacement - where service re-burns the printer's
            // original serial onto a new board - changes the CPU UUID and MAC, so the fingerprint
            // changes while the serial does not. GetPrinterCode then finds no row by fingerprint,
            // inserts, and hits UNIQUE constraint failed: SerialNumber -> 500. The firmware retries
            // the initial POST three times (registrator.hpp, starting_retries = 3) and then abandons
            // registration for good, so the printer becomes permanently unregisterable without
            // manual database surgery.
            //
            // Without the constraint the replacement board simply registers as a new printer, which
            // is honest - it is new hardware - and the old row is stale data rather than a blocker.
            entity.HasIndex(e => e.SerialNumber);

            // Registration polls GET /p/register until the code is claimed.
            entity.HasIndex(e => e.TemporaryCode);
        });

        builder.Entity<PrinterLiveState>(entity =>
        {
            // 1:1 with Printer, sharing the primary key.
            entity.HasOne(e => e.Printer)
                  .WithOne()
                  .HasForeignKey<PrinterLiveState>(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelemetrySample>(entity =>
        {
            // Every read is "this printer, this time range" — charts, stats, and the retention
            // sweep alike. Leading PrinterId so the range scan stays contiguous.
            entity.HasIndex(e => new { e.PrinterId, e.Timestamp });

            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrinterLiveSlotState>(entity =>
        {
            // Natural composite key: one row per printer per slot, which makes the merge an upsert.
            entity.HasKey(e => new { e.PrinterId, e.SlotNumber });

            entity.HasOne(e => e.PrinterLiveState)
                  .WithMany(e => e.Slots)
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelemetrySlotSample>(entity =>
        {
            // A sample cannot report the same slot twice; enforcing it also serves the
            // "slots for this sample" lookup.
            entity.HasIndex(e => new { e.TelemetrySampleId, e.SlotNumber })
                  .IsUnique();

            // Cascade matters for retention: the sweep issues a bulk delete against
            // TelemetrySamples, and the database removes the slot rows. That requires
            // PRAGMA foreign_keys = ON, which SqlitePragmaInterceptor sets per connection.
            entity.HasOne(e => e.TelemetrySample)
                  .WithMany(e => e.Slots)
                  .HasForeignKey(e => e.TelemetrySampleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrinterEvent>(entity =>
        {
            entity.HasIndex(e => new { e.PrinterId, e.Timestamp });

            // "What happened during job N" is the question the job view asks.
            entity.HasIndex(e => new { e.PrinterId, e.JobId });

            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Stored as text: readable in a raw SQLite session, and immune to reordering of
            // the enum. The volume does not justify the two bytes saved.
            entity.Property(e => e.EventType)
                  .HasConversion<string>();
        });
    }
}
