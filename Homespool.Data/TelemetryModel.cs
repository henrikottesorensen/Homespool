using System;

using Microsoft.EntityFrameworkCore;

using Homespool.Model.Entities;

namespace Homespool.Data;

/// <summary>
/// The mapping for the five entities the telemetry stream writes, shared by the two contexts that
/// can own them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two contexts map the same tables, and neither is redundant.</b>
/// <see cref="HomespoolDbContext"/> owns the schema and the migration, so its model must keep
/// describing these tables whatever the telemetry store is doing; <see cref="TelemetryDbContext"/>
/// is what actually reads and writes them, against either the same file or a private in-memory
/// database. Configuring both from one place is what stops an index or a converter being added to
/// one and not the other - a drift that would produce two disagreeing models over one set of tables.
/// </para>
/// <para>
/// <b>Whether the printer foreign keys are declared is the only difference between the two.</b> A
/// telemetry database held in memory has no <see cref="Printer"/> table to point at, so the
/// relationships that hang these rows off a printer cannot be declared there. Nothing is lost by it:
/// the columns and indexes are identical, and the deletion those cascades performed is done
/// explicitly instead - see <see cref="TelemetryDbContext"/>.
/// </para>
/// </remarks>
public static class TelemetryModel
{
    /// <summary>
    /// Maps <see cref="PrinterLiveState"/>, <see cref="PrinterLiveSlotState"/>,
    /// <see cref="TelemetrySample"/>, <see cref="TelemetrySlotSample"/> and
    /// <see cref="PrinterEvent"/>.
    /// </summary>
    /// <param name="builder">The model being built.</param>
    /// <param name="withPrinterForeignKeys">
    /// Whether a <see cref="Printer"/> table exists in this database to relate these rows to. False
    /// for a standalone telemetry database, which holds these five tables and nothing else.
    /// </param>
    public static void Configure(ModelBuilder builder, bool withPrinterForeignKeys)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<PrinterLiveState>(entity =>
        {
            if (withPrinterForeignKeys)
            {
                // 1:1 with Printer, sharing the primary key. Navigation-less on both sides - the FK and
                // cascade are all the relationship needs, and TelemetryWriter's long-lived instances of
                // this type must hold nothing a DbContext could have written into them (see the entity's
                // own remarks).
                entity.HasOne<Printer>()
                      .WithOne()
                      .HasForeignKey<PrinterLiveState>(e => e.PrinterId)
                      .OnDelete(DeleteBehavior.Cascade);
            }

            // Text, as everywhere else. This table is upserted rather than appended to and never
            // grows, so the wider column is paid once per printer instead of once per message.
            entity.Property(e => e.Status)
                  .HasConversion<string>();
        });

        builder.Entity<TelemetrySample>(entity =>
        {
            // Every read is "this printer, this time range" — charts, stats, and the retention
            // sweep alike. Leading PrinterId so the range scan stays contiguous.
            entity.HasIndex(e => new { e.PrinterId, e.Timestamp });

            if (withPrinterForeignKeys)
            {
                // Navigation-less: see the entity's PrinterId comment.
                entity.HasOne<Printer>()
                      .WithMany()
                      .HasForeignKey(e => e.PrinterId)
                      .OnDelete(DeleteBehavior.Cascade);
            }

            // Text, as everywhere else - and this is the one table where it costs anything, so the
            // measurement is here rather than left to be re-derived. One printer's full 14-day
            // retention window at 1 Hz is 1.2M rows: 139 MiB with an integer status against 146 MiB
            // with the name, so +4.6% while printing and +2.8% while idle. It cannot be worse than
            // that, because a 39-column row pays its ~39 bytes of header whether or not the columns
            // are null - the status is a few bytes on top of a row that is mostly overhead.
            //
            // Which is also why this column is the wrong place to economise: the table costs over
            // 100 MiB per printer per window either way, and that is the dense-sample-at-1-Hz
            // decision rather than this. Halving the rate or shortening retention saves ten times
            // what an integer here would.
            entity.Property(e => e.Status)
                  .HasConversion<string>();
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

            if (withPrinterForeignKeys)
            {
                // Navigation-less: see the entity's PrinterId comment.
                entity.HasOne<Printer>()
                      .WithMany()
                      .HasForeignKey(e => e.PrinterId)
                      .OnDelete(DeleteBehavior.Cascade);
            }

            // Stored as text: readable in a raw SQLite session, and immune to reordering of
            // the enum. The volume does not justify the two bytes saved.
            entity.Property(e => e.EventType)
                  .HasConversion<string>();

            // The same, and this row is the clearest argument for the rule: an event reading
            // EventType "StateChanged" beside Status 9 is half-decoded, and the half needing a
            // lookup is the half it was opened for.
            entity.Property(e => e.Status)
                  .HasConversion<string>();
        });
    }
}
