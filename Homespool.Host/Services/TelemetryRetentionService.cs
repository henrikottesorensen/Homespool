using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Services;

/// <summary>
/// Sweeps <see cref="TelemetrySample"/> rows older than
/// <see cref="StorageOptions.TelemetryRetentionDays"/>. <see cref="PrinterEvent"/> rows are never
/// swept — see AGENT-NOTES §5.
/// </summary>
/// <remarks>
/// <para>
/// Runs once at startup, then on an hourly timer thereafter. Startup-first matters because a
/// container that gets recycled more often than the timer's period would otherwise never sweep at
/// all — an hourly-only timer only fires after the first hour has elapsed.
/// </para>
/// <para>
/// A single bulk <c>ExecuteDeleteAsync</c> against <see cref="TelemetrySample"/> is sufficient on
/// its own: <see cref="TelemetrySlotSample"/> rows cascade at the SQLite level (foreign keys are
/// enabled via the connection string in
/// <see cref="DataServiceCollectionExtensions.AddHomespoolData"/>), not through EF's change
/// tracker — which a bulk delete bypasses entirely, so that enforcement is what makes this safe.
/// </para>
/// <para>
/// A failed sweep is caught and logged rather than left to crash the service: nothing restarts a
/// <see cref="BackgroundService"/>, and a single locked-database moment should cost one sweep, not
/// retention forever.
/// </para>
/// </remarks>
public sealed class TelemetryRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _options;
    private readonly ILogger<TelemetryRetentionService> _logger;

    public TelemetryRetentionService(IServiceScopeFactory scopeFactory,
                                     IOptions<StorageOptions> options,
                                     ILogger<TelemetryRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval);

        try
        {
            do
            {
                await SweepAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - stoppingToken fired while awaiting the timer.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        if (_options.TelemetryRetentionDays == 0)
        {
            _logger.LogDebug("Telemetry retention is disabled (TelemetryRetentionDays = 0); skipping sweep.");

            return;
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();

            DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.TelemetryRetentionDays);

            int deleted = await context.TelemetrySamples
                .Where(s => s.Timestamp < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Telemetry retention sweep deleted {Count} sample(s) older than {Cutoff:o}.",
                    deleted, cutoff);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Telemetry retention sweep failed; will retry on the next tick.");
        }
    }
}
