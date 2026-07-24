using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect.DTO.App;
using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;
using PrinterService.Model.Entities;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Persists what <see cref="MessageDispatcher"/> parses: merges telemetry into a per-printer
/// live-state cache, snapshots dense <see cref="TelemetrySample"/> rows from the merged result, and
/// buffers <see cref="PrinterEvent"/> rows — all through one bounded channel, batched, so socket
/// handlers never open a <see cref="PSDbContext"/> themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton with one reader.</b> Registered as both the sole <see cref="ITelemetrySink"/> and
/// the hosted <see cref="BackgroundService"/>, so <see cref="Enqueue(int,DateTimeOffset,TelemetryDTO)"/>
/// only ever does a non-blocking channel write; every merge, throttle decision and flush happens on
/// this one draining loop, which is what lets the live-state cache be a plain
/// <see cref="Dictionary{TKey,TValue}"/> with no locking.
/// </para>
/// <para>
/// <b>Channel capacity is derived from <see cref="StorageOptions.WriteBatchSize"/></b> rather than
/// configured separately — four batches' worth of headroom is enough for a flush to fall behind
/// briefly without losing anything, and one to tens of printers never come close to filling it.
/// <see cref="BoundedChannelFullMode.DropOldest"/> per the accepted trade (AGENT-NOTES §5): the
/// socket read loop must never block on a slow writer.
/// </para>
/// <para>
/// <b>One bad message must not stop persistence for every other printer.</b>
/// <see cref="PrinterStatusExtensions.ParseWireState"/> throws on a wire value outside the known
/// vocabulary; an uncaught throw from the drain loop would end this <see cref="BackgroundService"/>
/// permanently; nothing restarts it. Each item is processed in its own try/catch for exactly this
/// reason — logged and skipped, rather than risking the whole writer.
/// </para>
/// </remarks>
public sealed class TelemetryWriter : BackgroundService, ITelemetrySink
{
    /// <summary>Channel headroom as a multiple of one flush batch. See remarks above.</summary>
    private const int CapacityBatches = 4;

    private readonly Channel<TelemetryWriteItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _options;
    private readonly ILogger<TelemetryWriter> _logger;

    public TelemetryWriter(IServiceScopeFactory scopeFactory, IOptions<StorageOptions> options, ILogger<TelemetryWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;

        _channel = Channel.CreateBounded<TelemetryWriteItem>(new BoundedChannelOptions(Math.Max(_options.WriteBatchSize, 1) * CapacityBatches)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)
    {
        _channel.Writer.TryWrite(new TelemetryWriteItem.TelemetryItem(printerId, receivedAt, telemetry));
    }

    public void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)
    {
        _channel.Writer.TryWrite(new TelemetryWriteItem.EventItem(printerId, receivedAt, eventDto));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Dictionary<int, LiveStateCacheEntry> cache = new();
        List<TelemetrySample> pendingSamples = [];
        List<PrinterEvent> pendingEvents = [];
        HashSet<int> dirtyPrinterIds = [];

        using PeriodicTimer flushTimer = new(TimeSpan.FromSeconds(Math.Max(_options.WriteFlushIntervalSeconds, 0.05)));

        // Kept alive across loop iterations and only replaced once it actually fires - recreating it
        // on every pass through the outer loop would mean a busy printer's steady stream of channel
        // reads starves the timer branch forever, and low-traffic printers would never flush on
        // schedule.
        Task<bool> timerTick = flushTimer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Task<bool> channelReadable = _channel.Reader.WaitToReadAsync(stoppingToken).AsTask();

                Task<bool> completed = await Task.WhenAny(channelReadable, timerTick);

                if (completed == channelReadable)
                {
                    while (_channel.Reader.TryRead(out TelemetryWriteItem? item))
                    {
                        await ProcessItemAsync(item, cache, pendingSamples, pendingEvents, dirtyPrinterIds, stoppingToken);

                        if (pendingSamples.Count + pendingEvents.Count >= _options.WriteBatchSize)
                        {
                            await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, stoppingToken);
                        }
                    }
                }
                else
                {
                    await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, stoppingToken);

                    timerTick = flushTimer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - stoppingToken fired while awaiting the channel or the timer.
        }
    }

    private async Task ProcessItemAsync(TelemetryWriteItem item,
                                        Dictionary<int, LiveStateCacheEntry> cache,
                                        List<TelemetrySample> pendingSamples,
                                        List<PrinterEvent> pendingEvents,
                                        HashSet<int> dirtyPrinterIds,
                                        CancellationToken cancellationToken)
    {
        try
        {
            switch (item)
            {
                case TelemetryWriteItem.TelemetryItem telemetryItem:
                    await ProcessTelemetryAsync(telemetryItem, cache, pendingSamples, dirtyPrinterIds, cancellationToken);
                    break;

                case TelemetryWriteItem.EventItem eventItem:
                    ProcessEvent(eventItem, pendingEvents);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "[{PrinterId}] dropping one message that failed to process.", item.PrinterId);
        }
    }

    private async Task ProcessTelemetryAsync(TelemetryWriteItem.TelemetryItem item,
                                             Dictionary<int, LiveStateCacheEntry> cache,
                                             List<TelemetrySample> pendingSamples,
                                             HashSet<int> dirtyPrinterIds,
                                             CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(item.PrinterId, out LiveStateCacheEntry? entry))
        {
            entry = await HydrateAsync(item.PrinterId, cancellationToken);
            cache[item.PrinterId] = entry;
        }

        PrinterLiveStateMerger.Merge(entry.State, item.Data, item.ReceivedAt);
        dirtyPrinterIds.Add(item.PrinterId);

        // Printer.LoadedMaterial lives on a different table from PrinterLiveState, so it can't ride
        // along in the merge above; carried on the cache entry instead and applied at flush.
        if (item.Data.Material is { } material)
        {
            entry.PendingLoadedMaterial = material;
        }

        double throttle = _options.MinimumSampleIntervalSeconds;
        bool dueForSample = throttle <= 0
                          || entry.LastSampledAt is null
                          || (item.ReceivedAt - entry.LastSampledAt.Value).TotalSeconds >= throttle;

        // The throttle governs history density only - the live-state merge above always runs, so the
        // live view stays current even while samples are being skipped.
        if (dueForSample)
        {
            pendingSamples.Add(PrinterLiveStateMerger.ToSample(entry.State, item.ReceivedAt));
            entry.LastSampledAt = item.ReceivedAt;
        }
    }

    private static void ProcessEvent(TelemetryWriteItem.EventItem item, List<PrinterEvent> pendingEvents)
    {
        EventDTO dto = item.Data;

        pendingEvents.Add(new PrinterEvent
        {
            PrinterId = item.PrinterId,
            Timestamp = item.ReceivedAt,
            EventType = dto.EventType,
            Status = PrinterStatusExtensions.ParseWireState(dto.Status),
            JobId = dto.JobId,
            CommandId = dto.CommandId,
            Reason = dto.Reason,
            Payload = dto.Data?.GetRawText(),
        });
    }

    /// <summary>
    /// First sighting of a printer since this process started: reads its current
    /// <see cref="PrinterLiveState"/> (if any) so the merge has a real starting point instead of
    /// treating "never seen this process lifetime" as "never enrolled". A short-lived scope for the
    /// read alone - the long-lived cache entry it produces holds no <see cref="PSDbContext"/>.
    /// </summary>
    private async Task<LiveStateCacheEntry> HydrateAsync(int printerId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        PSDbContext context = scope.ServiceProvider.GetRequiredService<PSDbContext>();

        PrinterLiveState? existing = await context.PrinterLiveStates
            .Include(s => s.Slots)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PrinterId == printerId, cancellationToken);

        if (existing is not null)
        {
            LiveStateCacheEntry hydrated = new() { State = existing, ExistsInDatabase = true };

            foreach (PrinterLiveSlotState slot in existing.Slots)
            {
                hydrated.ExistingSlotNumbers.Add(slot.SlotNumber);
            }

            return hydrated;
        }

        return new LiveStateCacheEntry
        {
            State = new PrinterLiveState { PrinterId = printerId },
            ExistsInDatabase = false,
        };
    }

    /// <summary>
    /// Wraps <see cref="FlushAsync"/> the same way <see cref="ProcessItemAsync"/> wraps a single
    /// item: a flush failure (a locked database, a constraint violation) must not take down the
    /// whole background service and silently stop persistence for every printer. The buffers are
    /// deliberately left as they are on failure - the next successful flush picks up everything
    /// still pending, rather than the data being dropped along with the exception.
    /// </summary>
    private async Task SafeFlushAsync(Dictionary<int, LiveStateCacheEntry> cache,
                                      List<TelemetrySample> pendingSamples,
                                      List<PrinterEvent> pendingEvents,
                                      HashSet<int> dirtyPrinterIds,
                                      CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Telemetry flush failed; {SampleCount} samples and {EventCount} events remain pending for the next attempt.",
                pendingSamples.Count, pendingEvents.Count);
        }
    }

    /// <summary>
    /// Writes everything buffered since the last flush in one transaction, then clears the buffers.
    /// A no-op if nothing is pending, which the timer branch hits constantly on an idle deployment.
    /// </summary>
    /// <remarks>
    /// <b>Explicit per-entity <see cref="EntityState"/>, not <c>Update()</c>/<c>Add()</c>.</b> Those
    /// convenience methods decide Added-vs-Modified from whether an entity's key looks like a CLR
    /// default - which is useless here, since <see cref="PrinterLiveState"/> and
    /// <see cref="PrinterLiveSlotState"/> both key on real, non-default printer/slot numbers whether
    /// or not a row has ever been saved. Guessing wrong for a slot reported for the first time mid
    /// -life would silently issue an <c>UPDATE</c> that matches zero rows rather than an
    /// <c>INSERT</c> - no exception, just a permanently missing slot. <see cref="LiveStateCacheEntry"/>
    /// already knows exactly which rows exist, from <see cref="HydrateAsync"/> and from every prior
    /// flush, so it is used directly instead of asking EF to infer it.
    /// </remarks>
    private async Task FlushAsync(Dictionary<int, LiveStateCacheEntry> cache,
                                  List<TelemetrySample> pendingSamples,
                                  List<PrinterEvent> pendingEvents,
                                  HashSet<int> dirtyPrinterIds,
                                  CancellationToken cancellationToken)
    {
        if (pendingSamples.Count == 0 && pendingEvents.Count == 0 && dirtyPrinterIds.Count == 0)
        {
            return;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        PSDbContext context = scope.ServiceProvider.GetRequiredService<PSDbContext>();

        if (pendingSamples.Count > 0)
        {
            context.TelemetrySamples.AddRange(pendingSamples);
        }

        if (pendingEvents.Count > 0)
        {
            context.PrinterEvents.AddRange(pendingEvents);
        }

        foreach (int printerId in dirtyPrinterIds)
        {
            LiveStateCacheEntry entry = cache[printerId];

            context.Attach(entry.State);
            context.Entry(entry.State).State = entry.ExistsInDatabase ? EntityState.Modified : EntityState.Added;
            entry.ExistsInDatabase = true;

            foreach (PrinterLiveSlotState slot in entry.State.Slots)
            {
                bool slotExists = entry.ExistingSlotNumbers.Contains(slot.SlotNumber);
                context.Entry(slot).State = slotExists ? EntityState.Modified : EntityState.Added;
                entry.ExistingSlotNumbers.Add(slot.SlotNumber);
            }

            if (entry.PendingLoadedMaterial is { } material)
            {
                await context.Printers
                    .Where(p => p.Id == printerId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LoadedMaterial, material), cancellationToken);

                entry.PendingLoadedMaterial = null;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        pendingSamples.Clear();
        pendingEvents.Clear();
        dirtyPrinterIds.Clear();
    }

    private sealed class LiveStateCacheEntry
    {
        public required PrinterLiveState State { get; init; }

        public bool ExistsInDatabase { get; set; }

        public HashSet<int> ExistingSlotNumbers { get; } = [];

        public DateTimeOffset? LastSampledAt { get; set; }

        public string? PendingLoadedMaterial { get; set; }
    }
}
