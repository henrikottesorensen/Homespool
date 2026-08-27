using System.ComponentModel.DataAnnotations;

using Homespool.Model.Entities;

namespace Homespool.Data;

/// <summary>
/// Storage and ingest tuning, bound from the <c>Storage</c> configuration section.
/// </summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Apply pending EF migrations on startup. Default on: this is a self-hosted,
    /// single-instance tool and requiring operators to run a command after every upgrade is a
    /// poor trade. Turn it off if you would rather control schema changes yourself.
    /// </summary>
    /// <remarks>
    /// Auto-migrating is only safe because exactly one process owns the database. If that ever
    /// stops being true, this has to go — concurrent migrators corrupt schema state.
    /// </remarks>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// How long to keep <see cref="TelemetrySample"/> rows. Zero disables the retention sweep
    /// entirely.
    /// </summary>
    /// <remarks>
    /// <c>ushort</c>, not <c>int</c>: negative retention is meaningless, and an unsigned type
    /// rules it out at the config-binding level instead of needing a runtime check. 65,535 days
    /// (~180 years) is a ceiling nobody will ever hit but zero still reads unambiguously as
    /// "off".
    /// </remarks>
    public ushort TelemetryRetentionDays { get; set; } = 14;

    /// <summary>
    /// How long to keep <see cref="PrinterEvent"/> rows. Zero disables the age sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own setting rather than <see cref="TelemetryRetentionDays"/></b>, because the two
    /// differ in both volume and value: samples are a dense stream nobody reads past a chart's
    /// window, events are sparse and each one is a thing that happened. A year is far past anything
    /// that can be reached - the printer page shows the most recent handful, and the queue's
    /// reconciler walks forward from a watermark, so neither can see an older row.
    /// </para>
    /// <para>
    /// <b>Age alone does not bound the table</b>, which is what
    /// <see cref="MaxEventsPerPrinter"/> is for; see its remarks.
    /// </para>
    /// </remarks>
    public ushort EventRetentionDays { get; set; } = 365;

    /// <summary>
    /// The most <see cref="PrinterEvent"/> rows to keep for any one printer, oldest dropped first.
    /// Zero disables the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Age and count bound different things, and neither substitutes for the other.</b> A window
    /// an operator would actually choose is measured in days, and a printer emitting at the
    /// transport's ceiling fills a disk long inside it - so age bounds ordinary growth and this
    /// bounds the worst case. Whichever binds first wins.
    /// </para>
    /// <para>
    /// <b>Per printer, not a total.</b> A global cap would let one chatty printer evict every other
    /// printer's events, which is the failure a global rate limiter has - strictly worse than no
    /// limiting. The limiter cannot partition because
    /// it runs before authentication and the only identity available there is forgeable; this sweep
    /// runs over rows already attributed to a printer, so it can.
    /// </para>
    /// <para>
    /// <b>Evicting an unread event costs a round-trip, not an arrival.</b>
    /// <c>QueueAdvancer</c> learns of arrivals from <c>FILE_INFO</c> events, and a cap can drop one
    /// before it is read. The queue re-asks the printer what is on the drive and adopts a file
    /// already there, so the cost is a pass where the queue looks stuck - which is why the floor is
    /// a tuning question rather than a correctness one.
    /// </para>
    /// </remarks>
    [Range(0, 10_000_000)]
    public int MaxEventsPerPrinter { get; set; } = 10_000;

    /// <summary>
    /// Minimum seconds between stored samples per printer. Zero (default) stores every message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Present as an escape hatch, not because it is expected to be needed: at one-to-tens of
    /// printers a 1 Hz stream is roughly 86k rows/printer/day, which SQLite handles without
    /// complaint. Raise it if a large fleet or a slow disk changes that.
    /// </para>
    /// <para>
    /// <b>The case that makes it worth having is an SD card</b>, which wears out by being written to
    /// rather than by age, and on which telemetry is the largest single source of writes. A value of
    /// 10 removes about 90% of them and still leaves a graph somebody can read; what it costs is
    /// resolution, so a spike shorter than the interval may never be recorded. <b>Shortening
    /// retention instead is not a substitute — a delete is a write too.</b>
    /// </para>
    /// </remarks>
    [Range(0, 86_400)]
    public double MinimumSampleIntervalSeconds { get; set; }

    /// <summary>Rows buffered before the writer flushes a batch.</summary>
    [Range(1, 100_000)]
    public int WriteBatchSize { get; set; } = 500;

    /// <summary>Maximum seconds a buffered row waits before being flushed.</summary>
    [Range(0, 3_600)]
    public double WriteFlushIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// How long a blocked writer waits for the database lock before failing, in milliseconds.
    /// </summary>
    public int BusyTimeoutMilliseconds { get; set; } = 5000;
}
