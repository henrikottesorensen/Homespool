using Microsoft.EntityFrameworkCore;

using Homespool.Data;

namespace Homespool.Host.Test;

/// <summary>
/// Builds a <see cref="TelemetryDbContext"/> over a test's own SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same file the test's <see cref="HomespoolDbContext"/> uses</b>, which is the arrangement a
/// deployment has by default: the telemetry context is a second door onto tables the migration
/// already created, and only <c>StorageOptions.TelemetryInMemory</c> moves them elsewhere. A test
/// that seeds telemetry through either context is therefore seeding the same rows.
/// </para>
/// <para>
/// The exception is the tests that exercise the in-memory mode itself, which build their own
/// connection string and are the only place the two databases differ.
/// </para>
/// </remarks>
internal static class TestTelemetryContext
{
    /// <summary>A telemetry context over the SQLite file at <paramref name="databasePath"/>.</summary>
    public static TelemetryDbContext For(string databasePath)
    {
        DbContextOptions<TelemetryDbContext> options = new DbContextOptionsBuilder<TelemetryDbContext>()
                                                       .UseSqlite($"Data Source={databasePath}")
                                                       .Options;

        return new TelemetryDbContext(options);
    }

    /// <summary>
    /// A telemetry context over whatever database <paramref name="context"/> is using.
    /// </summary>
    /// <remarks>
    /// The form nearly every test wants: it keeps the two contexts pointing at one file without the
    /// test having to hold the path, and it cannot drift if the path is later changed in one place.
    /// </remarks>
    public static TelemetryDbContext For(HomespoolDbContext context)
    {
        return ForConnectionString(context.Database.GetConnectionString()!);
    }

    /// <summary>A telemetry context over an explicit connection string.</summary>
    public static TelemetryDbContext ForConnectionString(string connectionString)
    {
        DbContextOptions<TelemetryDbContext> options = new DbContextOptionsBuilder<TelemetryDbContext>()
                                                       .UseSqlite(connectionString)
                                                       .Options;

        return new TelemetryDbContext(options);
    }
}
