using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrinterService.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PSDbContext"/> against SQLite, with pragmas applied per connection
    /// and <see cref="StorageOptions"/> bound from configuration.
    /// </summary>
    public static IServiceCollection AddPrinterServiceData(this IServiceCollection services,
                                                           IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        StorageOptions storage = configuration.GetSection(StorageOptions.SectionName)
                                              .Get<StorageOptions>()
                                 ?? new StorageOptions();

        string? connectionString = configuration.GetConnectionString("PrinterServiceDb");

        services.AddDbContext<PSDbContext>(ef =>
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
    public static void MigratePrinterServiceData(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();

        StorageOptions storage = scope.ServiceProvider
                                      .GetRequiredService<IOptions<StorageOptions>>()
                                      .Value;

        ILogger logger = scope.ServiceProvider
                              .GetRequiredService<ILoggerFactory>()
                              .CreateLogger(typeof(DataServiceCollectionExtensions).FullName!);

        if (!storage.AutoMigrate)
        {
            logger.LogInformation("Automatic migration is disabled; skipping. Apply migrations manually.");

            return;
        }

        PSDbContext context = scope.ServiceProvider.GetRequiredService<PSDbContext>();

        logger.LogInformation("Applying pending database migrations.");

        context.Database.Migrate();

        logger.LogInformation("Database schema is up to date.");
    }
}
