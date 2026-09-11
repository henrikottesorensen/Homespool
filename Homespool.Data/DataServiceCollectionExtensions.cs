using System;
using System.Data;
using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HomespoolDbContext"/> against SQLite, with pragmas applied per connection
    /// and <see cref="StorageOptions"/> bound from configuration.
    /// </summary>
    public static IServiceCollection AddHomespoolData(this IServiceCollection services,
                                                      IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        StorageOptions storage = configuration.GetSection(StorageOptions.SectionName)
                                              .Get<StorageOptions>()
                                 ?? new StorageOptions();

        string? connectionString = configuration.GetConnectionString("HomespoolDb");

        // Foreign key enforcement is a connection-string keyword, so it is applied by the driver as
        // the connection opens rather than issued as a statement afterwards. That matters because the
        // retention sweep relies on ON DELETE CASCADE to remove slot rows: if
        // enforcement were ever missed, those rows would leak silently. Setting it here also means an
        // operator cannot omit it by editing the connection string in configuration.
        connectionString = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true,
        }.ToString();

        services.AddDbContext<HomespoolDbContext>(ef =>
        {
            ef.UseSqlite(connectionString);
            ef.AddInterceptors(new SqlitePragmaInterceptor(storage.BusyTimeoutMilliseconds));
        });

        AddTelemetryData(services, storage, connectionString);

        return services;
    }

    /// <summary>
    /// Registers <see cref="TelemetryDbContext"/> against either the application database or a private
    /// in-memory one, per <see cref="StorageOptions.TelemetryInMemory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off, this is the same file and the same tables</b> - the context is a second door onto rows
    /// the migration already created, so nothing about what lands on disk changes.
    /// </para>
    /// <para>
    /// <b>On, telemetry is diverted to a database that never touches the disk.</b> Foreign keys are
    /// enabled there for the same reason they are on the file: the retention sweep deletes samples in
    /// bulk and relies on the database to take their slot rows with them.
    /// </para>
    /// </remarks>
    private static void AddTelemetryData(IServiceCollection services,
                                         StorageOptions storage,
                                         string applicationConnectionString)
    {
        if (!storage.TelemetryInMemory)
        {
            services.AddDbContext<TelemetryDbContext>(ef =>
            {
                ef.UseSqlite(applicationConnectionString);
                ef.AddInterceptors(new SqlitePragmaInterceptor(storage.BusyTimeoutMilliseconds));
            });

            return;
        }

        // Unique per host: a shared in-memory database is scoped to the process, and the end-to-end
        // suite builds a host per test inside one. See TelemetryKeepAlive.
        string telemetryConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"HomespoolTelemetry-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();

        // Registered through a factory so the container owns the lifetime and disposes it at
        // shutdown. It is resolved during startup, before the tables are created, because
        // constructing it is what brings the database into existence.
        services.AddSingleton(_ => new TelemetryKeepAlive(telemetryConnectionString));

        // No pragma interceptor. Its job is the busy timeout, which is about waiting for a writer to
        // release a file lock - there is no file here, and the shared cache serialises access itself.
        services.AddDbContext<TelemetryDbContext>(ef => ef.UseSqlite(telemetryConnectionString));
    }

    /// <summary>
    /// Applies pending migrations if <see cref="StorageOptions.AutoMigrate"/> is set, having first
    /// refused a database this build did not stamp.
    /// </summary>
    /// <remarks>
    /// Safe only because a single process owns the database. See <see cref="StorageOptions"/>. The
    /// refusal is <see cref="MigrationHistoryGuard"/>, which exists because this project regenerates
    /// its migration in place and the resulting failure is otherwise unreadable.
    /// </remarks>
    public static void MigrateHomespoolData(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();

        StorageOptions storage = scope.ServiceProvider
                                      .GetRequiredService<IOptions<StorageOptions>>()
                                      .Value;

        ILogger logger = scope.ServiceProvider
                              .GetRequiredService<ILoggerFactory>()
                              .CreateLogger(typeof(DataServiceCollectionExtensions).FullName!);

        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // Before the AutoMigrate check on purpose: WAL is required whether or not this process is the
        // one applying schema changes, and it must be set before Migrate() opens its read-only probe.
        EnsureWriteAheadLogging(context, logger);

        // An in-memory telemetry database starts empty every time, so it is created rather than
        // migrated - there is no history to carry forward and nothing to stamp. Before the
        // AutoMigrate check as well: that flag decides who owns changes to the durable schema, and an
        // operator who has taken that on has not thereby volunteered to create a database that only
        // exists inside this process.
        EnsureTelemetryStore(scope.ServiceProvider, storage, logger);

        // Also before it, and for a related reason: AutoMigrate says who applies schema changes, not
        // whether the schema is the right one. A database stamped by another build is fatal either
        // way, and with the flag off nothing else would ever notice.
        MigrationHistoryGuard.Verify(context);

        if (!storage.AutoMigrate)
        {
            logger.LogInformation("Automatic migration is disabled; skipping. Apply migrations manually.");

            return;
        }

        logger.LogInformation("Applying pending database migrations.");

        context.Database.Migrate();

        logger.LogInformation("Database schema is up to date.");
    }

    /// <summary>
    /// Creates the five telemetry tables in the in-memory database, when that is where they live.
    /// </summary>
    /// <remarks>
    /// <b>A no-op unless <see cref="StorageOptions.TelemetryInMemory"/> is set.</b> Against the
    /// application file those tables are the migration's, and calling <c>EnsureCreated</c> on a
    /// database that already has them would at best do nothing and at worst disagree with the
    /// migration about what they are.
    /// </remarks>
    private static void EnsureTelemetryStore(IServiceProvider services, StorageOptions storage, ILogger logger)
    {
        if (!storage.TelemetryInMemory)
        {
            return;
        }

        // Resolved so its construction is not deferred past the first write. It is registered as an
        // instance, so this only asserts that the database is up.
        services.GetRequiredService<TelemetryKeepAlive>();

        TelemetryDbContext telemetry = services.GetRequiredService<TelemetryDbContext>();
        telemetry.Database.EnsureCreated();

        logger.LogInformation(
            "Telemetry is held in memory only: samples, events and live state are bounded by MaxSamplesPerPrinter " +
            "and MaxEventsPerPrinter, and are discarded when this process stops. Nothing is written to disk for them, " +
            "which is the point - see StorageOptions.TelemetryInMemory. Live state alone is saved at shutdown.");
    }

    /// <summary>
    /// Applies <c>journal_mode=WAL</c> once, on a read-write connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Journal mode is persisted in the database file, so this only needs doing once - and it must
    /// <b>not</b> be done per connection. <see cref="SqlitePragmaInterceptor"/> used to issue it on
    /// every open, which crashed the service at startup against any database not already in WAL mode:
    /// <c>Migrator.Migrate()</c> calls <c>SqliteDatabaseCreator.Exists()</c>, which opens read-only so
    /// that testing for existence cannot create a file, and switching journal mode is a write.
    /// </para>
    /// <para>
    /// It stayed hidden because the statement is a no-op read once the database is already WAL, so a
    /// database created by this service worked forever while a restored backup - <c>.backup</c>,
    /// a <c>.dump</c> reimport, most GUI exports - failed immediately. Run before
    /// <see cref="RelationalDatabaseFacadeExtensions.Migrate(DatabaseFacade)"/> and regardless of
    /// <see cref="StorageOptions.AutoMigrate"/>,
    /// since WAL is required whether or not this process owns schema changes.
    /// </para>
    /// </remarks>
    private static void EnsureWriteAheadLogging(HomespoolDbContext context, ILogger logger)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool wasClosed = connection.State != ConnectionState.Open;

        try
        {
            if (wasClosed)
            {
                connection.Open();
            }

            using DbCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";

            string? mode = command.ExecuteScalar() as string;

            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected SQLite journal mode 'wal' but the database reported '{mode}'. " +
                    "Write-ahead logging is required: the telemetry writer commits continuously and " +
                    "readers must not block on it.");
            }

            logger.LogInformation("SQLite journal mode is {JournalMode}.", mode);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"Could not enable write-ahead logging on '{connection.DataSource}'. The database file " +
                "and the directory containing it must both be writable by this service, because WAL " +
                "creates -wal and -shm files alongside the database. In Docker this usually means the " +
                "mounted volume is owned by root while the container runs as a non-root user.",
                ex);
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }
    }
}
