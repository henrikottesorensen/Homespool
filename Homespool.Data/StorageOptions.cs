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
    /// entirely. Events are never swept.
    /// </summary>
    /// <remarks>
    /// <c>ushort</c>, not <c>int</c>: negative retention is meaningless, and an unsigned type
    /// rules it out at the config-binding level instead of needing a runtime check. 65,535 days
    /// (~180 years) is a ceiling nobody will ever hit but zero still reads unambiguously as
    /// "off".
    /// </remarks>
    public ushort TelemetryRetentionDays { get; set; } = 14;

    /// <summary>
    /// Minimum seconds between stored samples per printer. Zero (default) stores every message.
    /// </summary>
    /// <remarks>
    /// Present as an escape hatch, not because it is expected to be needed: at one-to-tens of
    /// printers a 1 Hz stream is roughly 86k rows/printer/day, which SQLite handles without
    /// complaint. Raise it if a large fleet or a slow disk changes that.
    /// </remarks>
    public double MinimumSampleIntervalSeconds { get; set; }

    /// <summary>Rows buffered before the writer flushes a batch.</summary>
    public int WriteBatchSize { get; set; } = 500;

    /// <summary>Maximum seconds a buffered row waits before being flushed.</summary>
    public double WriteFlushIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// How long a blocked writer waits for the database lock before failing, in milliseconds.
    /// </summary>
    public int BusyTimeoutMilliseconds { get; set; } = 5000;
}
