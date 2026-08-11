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
        // retention sweep relies on ON DELETE CASCADE to remove slot rows (see AGENT-NOTES §12): if
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

        return services;
    }

    /// <summary>
    /// Applies pending migrations if <see cref="StorageOptions.AutoMigrate"/> is set.
    /// </summary>
    /// <remarks>
    /// Safe only because a single process owns the database. See <see cref="StorageOptions"/>.
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

        // Before the AutoMigrate check on purpose: WAL is required whether or not this process is the
        // one applying schema changes, and it must be set before Migrate() opens its read-only probe.
        EnsureWriteAheadLogging(scope.ServiceProvider.GetRequiredService<HomespoolDbContext>(), logger);

        if (!storage.AutoMigrate)
        {
            logger.LogInformation("Automatic migration is disabled; skipping. Apply migrations manually.");

            return;
        }

        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        logger.LogInformation("Applying pending database migrations.");

        context.Database.Migrate();

        logger.LogInformation("Database schema is up to date.");
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
