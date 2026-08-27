using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Sweeps <see cref="TelemetrySample"/> rows older than
/// <see cref="StorageOptions.TelemetryRetentionDays"/>, and <see cref="PrinterEvent"/> rows past
/// either <see cref="StorageOptions.EventRetentionDays"/> or
/// <see cref="StorageOptions.MaxEventsPerPrinter"/>.
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
    private readonly IOptionsMonitor<StorageOptions> _options;
    private readonly ILogger<TelemetryRetentionService> _logger;

    public TelemetryRetentionService(IServiceScopeFactory scopeFactory,
                                     IOptionsMonitor<StorageOptions> options,
                                     ILogger<TelemetryRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
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

    /// <summary>
    /// One pass: samples by age, events by age, then events by per-printer count.
    /// </summary>
    /// <remarks>
    /// <b>Each sweep decides for itself whether it is enabled</b>, rather than one guard at the top.
    /// The three knobs are independent and zero means off for each - a deployment keeping samples for
    /// a fortnight and events for a year is the ordinary case, and an early return on the first would
    /// silently disable the others.
    /// </remarks>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            await SweepSamplesAsync(context, cancellationToken);
            await SweepEventsByAgeAsync(context, cancellationToken);
            await SweepEventsByCountAsync(context, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Retention sweep failed; will retry on the next tick.");
        }
    }

    private async Task SweepSamplesAsync(HomespoolDbContext context, CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.TelemetryRetentionDays == 0)
        {
            _logger.LogDebug("Telemetry retention is disabled (TelemetryRetentionDays = 0); skipping sweep.");

            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.CurrentValue.TelemetryRetentionDays);

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

    private async Task SweepEventsByAgeAsync(HomespoolDbContext context, CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.EventRetentionDays == 0)
        {
            _logger.LogDebug("Event age retention is disabled (EventRetentionDays = 0); skipping sweep.");

            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.CurrentValue.EventRetentionDays);

        int deleted = await context.PrinterEvents
                                   .Where(e => e.Timestamp < cutoff)
                                   .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Event retention sweep deleted {Count} event(s) older than {Cutoff:o}.",
                deleted, cutoff);
        }
    }

    /// <summary>
    /// Trims each printer back to <see cref="StorageOptions.MaxEventsPerPrinter"/> rows, oldest
    /// first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not one bulk delete, and that is inherent.</b> The sample sweep is a single statement
    /// because its cutoff is one value for the whole table; a per-printer cap has a different
    /// threshold per printer, so it is a pass each. Cheap on a healthy table - the threshold query
    /// rides the <c>(PrinterId, Id)</c> ordering and most printers return nothing to delete.
    /// </para>
    /// <para>
    /// <b>By <c>Id</c> rather than <c>Timestamp</c></b>, because ties matter here where they do not
    /// for a cutoff: rows arriving in the same millisecond must still have a definite oldest, or the
    /// trim removes an arbitrary subset and leaves the count wrong.
    /// </para>
    /// </remarks>
    private async Task SweepEventsByCountAsync(HomespoolDbContext context, CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.MaxEventsPerPrinter <= 0)
        {
            _logger.LogDebug("Event count cap is disabled (MaxEventsPerPrinter = {Cap}); skipping sweep.",
                             _options.CurrentValue.MaxEventsPerPrinter);

            return;
        }

        List<int> printerIds = await context.PrinterEvents
                                            .Select(e => e.PrinterId)
                                            .Distinct()
                                            .ToListAsync(cancellationToken);

        foreach (int printerId in printerIds)
        {
            // The id of the newest row to keep. Null when this printer has fewer rows than the cap,
            // which is the ordinary case and costs one indexed read.
            long threshold = await context.PrinterEvents
                                          .Where(e => e.PrinterId == printerId)
                                          .OrderByDescending(e => e.Id)
                                          .Skip(_options.CurrentValue.MaxEventsPerPrinter - 1)
                                          .Select(e => e.Id)
                                          .FirstOrDefaultAsync(cancellationToken);

            if (threshold == 0)
            {
                continue;
            }

            int deleted = await context.PrinterEvents
                                       .Where(e => e.PrinterId == printerId && e.Id < threshold)
                                       .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Event cap sweep deleted {Count} event(s) for printer {PrinterId}, keeping the newest {Cap}.",
                    deleted, printerId, _options.CurrentValue.MaxEventsPerPrinter);
            }
        }
    }
}
