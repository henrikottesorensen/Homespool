using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Persists what <see cref="MessageDispatcher"/> parses: merges telemetry into a per-printer
/// live-state cache, snapshots dense <see cref="TelemetrySample"/> rows from the merged result, and
/// buffers <see cref="PrinterEvent"/> rows — all through one bounded channel, batched, so socket
/// handlers never open a <see cref="HSDbContext"/> themselves.
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
/// socket read loop must never block on a slow writer. Dropping is silent to the printer - it never
/// discovers this happened - but not to an operator: the first drop of an overload episode logs a
/// warning immediately, and further drops are aggregated into at most one summary per
/// <see cref="DropWarningInterval"/>, with the lifetime total always visible in
/// <see cref="TelemetryHealthSnapshot.DroppedMessages"/>. Per-drop logging was the original design
/// and it failed its first load test: 20 seconds of blast telemetry produced 722,973 warnings and a
/// 1.0 GB log - a self-feeding cycle, since the log I/O steals exactly the capacity the writer is
/// already short of, and it fires on the producer's thread, taxing the message path itself
/// (notes/fake-printer-harness.md, the blast run).
/// </para>
/// <para>
/// <b>One bad message must not stop persistence for every other printer.</b>
/// <see cref="PrinterStatusExtensions.ParseWireState"/> throws on a wire value outside the known
/// vocabulary; an uncaught throw from the drain loop would end this <see cref="BackgroundService"/>
/// permanently; nothing restarts it. Each item is processed in its own try/catch for exactly this
/// reason — logged and skipped, rather than risking the whole writer.
/// </para>
/// <para>
/// <b>Two independent safety nets guard against unbounded growth, for two different failure modes.</b>
/// The bounded channel (above) protects against a <em>brief</em> lag - the writer falling a little
/// behind a burst - by dropping the oldest queued message once <see cref="CapacityBatches"/> worth of
/// headroom fills. <see cref="TrimExcessPendingSamples"/> protects against the opposite case: flushes
/// that keep <em>failing</em> (a locked or unreachable database), where <see cref="SafeFlushAsync"/>
/// deliberately keeps the buffers populated for a retry, and nothing would otherwise stop them growing
/// for as long as the outage lasts. Only samples are ever trimmed this way - dense, redundant,
/// already subject to retention - never <see cref="PrinterEvent"/> rows, which are rare, retained
/// indefinitely, and each one a discrete fact that never repeats.
/// </para>
/// <para>
/// <b>Shutdown is completion, not cancellation.</b> <see cref="StopAsync"/> closes the channel's
/// writer end and lets the drain loop finish on its own terms: it exits when the channel reports
/// "completed and empty", and <em>no cancellation token is threaded into the work it does on the way
/// out</em>. That is what makes losing buffered data on shutdown structurally impossible rather than
/// something the loop has to defend against.
/// </para>
/// <para>
/// This replaced a cancellation-driven shutdown that needed three separate fixes for three separate
/// leaks - unflushed in-memory batches, items left unread in the channel, and an item already dequeued
/// whose <see cref="HydrateAsync"/> read was sliced through mid-flight by the token - each found only
/// after the previous one was fixed. All three are unrepresentable here: there is nothing to drain in
/// a <c>finally</c> because the loop drains by definition, and nothing can interrupt an item
/// mid-processing because nothing cancels it. The failure that started it was real: an MK3.5 session's
/// telemetry and command-ack events from an active print vanished across a dev-server restart.
/// <c>HostOptions.ShutdownTimeout</c> remains the backstop if the database is genuinely stuck.
/// </para>
/// </remarks>
public sealed class TelemetryWriter : BackgroundService, ITelemetrySink, ITelemetryHealthSource
{
    /// <summary>Channel headroom as a multiple of one flush batch. See remarks above.</summary>
    private const int CapacityBatches = 4;

    /// <summary>
    /// How many batches' worth of samples the in-memory buffer may hold before the oldest are
    /// discarded to cap memory - deliberately far larger than <see cref="CapacityBatches"/>, since
    /// this is a safety net for a database that has been failing to accept flushes for a while, not
    /// routine headroom. See the class remarks for why samples give way before events.
    /// </summary>
    private const int MaxPendingSampleBatches = 20;

    /// <summary>
    /// The same ceiling for events, and the last thing to give way.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="MaxPendingSampleBatches"/> in raw count, yet events still survive
    /// several times longer in wall-clock terms, which is the ordering that actually matters: a
    /// printing printer emits telemetry roughly ten times as often as it emits events, so the sample
    /// buffer fills far faster. Both buffers are shared across every connected printer, so more
    /// printers make each ceiling arrive sooner rather than making it larger.
    ///
    /// Sized as a bound on catastrophe, not as working headroom. Anything that outlives it is a
    /// database outage measured in hours, which is a bigger problem than the events being dropped.
    /// </remarks>
    private const int MaxPendingEventBatches = 10;

    /// <summary>
    /// How many times the shutdown flush is attempted before the buffers are declared lost.
    /// </summary>
    /// <remarks>
    /// The only retried flush in the class, because it is the only one with no next attempt behind
    /// it. While running, a failed flush costs nothing - the buffers are kept and the timer comes
    /// round again seconds later. At shutdown the loop has already exited, so a single transient
    /// failure (SQLite busy, a WAL checkpoint, a connection not yet released by whatever else just
    /// used the file) silently takes everything buffered with it.
    ///
    /// Bounded and short: a database that is genuinely down will not recover inside a shutdown, and
    /// nothing here should be able to hold the process open for long.
    /// </remarks>
    private const int FinalFlushAttempts = 3;

    /// <summary>
    /// The <c>FILE_INFO</c> data keys that are stored as <c>null</c> rather than kept - both of them
    /// copies of the uploaded gcode's own content, relayed by firmware rather than produced by it.
    /// See <see cref="FormatPayload"/> for the reasoning and for how to get them back.
    /// </summary>
    private static readonly string[] StrippedProperties = ["preview", "objects_info"];

    private static readonly TimeSpan FinalFlushRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly Channel<TelemetryWriteItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _options;
    private readonly ILogger<TelemetryWriter> _logger;

    // Both wire-rate log sites in this class go through a LogThrottle: drops are recorded on
    // whatever producer thread hit the full channel, processing failures on the drain loop, and
    // either can arrive at wire rate (the second is attacker-driveable - a stream of deliberately
    // unprocessable messages). See LogThrottle's remarks for the blast-test numbers behind this.
    private readonly Services.LogThrottle _dropWarnings = new(TimeSpan.FromSeconds(10));
    private readonly Services.LogThrottle _processingFailureWarnings = new(TimeSpan.FromSeconds(10));

    // Written only by the drain loop, read by whatever calls Current (a health check, on a request
    // thread). Published as one immutable snapshot rather than read field by field - see PublishHealth.
    private volatile TelemetryHealthSnapshot _health = TelemetryHealthSnapshot.Initial;
    private DateTimeOffset? _lastFlushAt;
    private int _consecutiveFlushFailures;
    private long _discardedEvents;

    public TelemetryWriter(IServiceScopeFactory scopeFactory, IOptions<StorageOptions> options, ILogger<TelemetryWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;

        // itemDropped fires synchronously, on the producer's thread, exactly when DropOldest
        // actually discards something - not an approximation from watching queue depth. Logged as
        // a warning rather than silently: a drop means the writer is falling behind the printer(s)
        // it's serving, which is worth an operator's attention, not just a debugging footnote.
        _channel = Channel.CreateBounded<TelemetryWriteItem>(new BoundedChannelOptions(Math.Max(_options.WriteBatchSize, 1) * CapacityBatches)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, OnItemDropped);
    }

    /// <summary>
    /// Minimum spacing between drop warnings. An <c>init</c> test seam rather than configuration,
    /// on the <c>ActorDrainTimeout</c> precedent: raising it loses nothing (counts aggregate into
    /// the next summary), lowering it re-creates the log flood, so no operator value is
    /// meaningfully right.
    /// </summary>
    public TimeSpan DropWarningInterval
    {
        get => _dropWarnings.Interval;
        init => _dropWarnings.Interval = value;
    }

    /// <summary>Same seam for the per-item processing-failure Error. See <see cref="DropWarningInterval"/>.</summary>
    public TimeSpan ProcessingFailureWarningInterval
    {
        get => _processingFailureWarnings.Interval;
        init => _processingFailureWarnings.Interval = value;
    }

    /// <summary>
    /// The event's <c>data</c> as it goes into the row - verbatim, except that the
    /// <see cref="StrippedProperties"/> of a <c>FILE_INFO</c> are replaced with <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both stripped keys are copies of the uploaded gcode's own content</b>, relayed by firmware
    /// rather than produced by it - <c>objects_info</c> is verified byte-identical between the file we
    /// hold and the wire, and <c>preview</c> is the thumbnail the slicer embedded. An event log should
    /// not carry a second copy of a file we already store, and a plate view that needs this wants it
    /// from a place built to serve it, not from a row in the event history (Henrik, 2026-07-27).
    /// </para>
    /// <para>
    /// <b>The size case, for <c>preview</c> specifically:</b> a base64 PNG measured at 47-89 KB
    /// against the 1-3 KB of everything else in the object. <see cref="PrinterEvent"/> rows are kept
    /// indefinitely (retention sweeps only <see cref="TelemetrySample"/>), and these events are
    /// <b>not</b> something we ask for: both a Connect-initiated transfer and a LAN upload through
    /// PrusaLink write to the same <c>ChangedPath</c> slot the Connect planner drains, so the printer
    /// volunteers one per file that appears on the drive - twice for the same file in one captured
    /// session. <c>objects_info</c> is far smaller (455-2 464 bytes measured) and goes for the
    /// duplication reason alone.
    /// </para>
    /// <para>
    /// <b>Why null rather than removed:</b> firmware omits both keys entirely when a file has no
    /// thumbnail or no labelled objects (render.cpp:791-795 for the preview; <c>objects_info</c> is
    /// simply absent from the gcode unless the slicer's "label objects" option is on). Deleting the
    /// key would make "we dropped it" indistinguishable from "there wasn't one". A null records that
    /// it existed, for 17 bytes, so a reader knows to go and fetch it properly.
    /// </para>
    /// <para>
    /// <b>Where to get them back:</b> a <c>SEND_FILE_INFO</c> for the path re-requests both from the
    /// printer, and works whatever the format. Reading our own stored upload also works, cheaply for
    /// plain gcode - the header sits near the end of the file - but needs a decompressor for
    /// <c>.bgcode</c>, which nothing here has.
    /// </para>
    /// </remarks>
    private static string? FormatPayload(JsonElement? data)
    {
        if (data is not { } element)
        {
            return null;
        }

        // The overwhelmingly common case: telemetry-rate events carry neither key, pay two lookups,
        // and keep their original text with nothing re-serialized.
        if (element.ValueKind != JsonValueKind.Object || !CarriesStrippedContent(element))
        {
            return element.GetRawText();
        }

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (Array.IndexOf(StrippedProperties, property.Name) >= 0)
                {
                    writer.WriteNull(property.Name);

                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool CarriesStrippedContent(JsonElement element)
    {
        foreach (string name in StrippedProperties)
        {
            if (element.TryGetProperty(name, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// First sighting of a printer since this process started: reads its current
    /// <see cref="PrinterLiveState"/> (if any) so the merge has a real starting point instead of
    /// treating "never seen this process lifetime" as "never enrolled". A short-lived scope for the
    /// read alone - the long-lived cache entry it produces holds no <see cref="HSDbContext"/>.
    /// </summary>
    private void OnItemDropped(TelemetryWriteItem dropped)
    {
        // Always counted (the health snapshot reports the exact total); logged at most once per
        // DropWarningInterval, whatever the drop rate.
        if (_dropWarnings.Record() is not { } window)
        {
            return;
        }

        string kind = dropped is TelemetryWriteItem.EventItem ? "event" : "telemetry";

        if (window.IsFirstOccurrence)
        {
            // The first drop of the process warns immediately and in full detail - an operator
            // should hear about the writer falling behind the moment it starts, not a window later.
            _logger.LogWarning(
                "Dropped a {Kind} message for printer {PrinterId} (received {ReceivedAt:o}) - the write channel is full, meaning the writer cannot keep up with the incoming rate.",
                kind, dropped.PrinterId, dropped.ReceivedAt);

            return;
        }

        _logger.LogWarning(
            "Dropped {Count} message(s) in the last {ElapsedSeconds:F0}s - the write channel is still full. Latest was a {Kind} message for printer {PrinterId}; {TotalDropped} dropped since startup.",
            window.Count, window.Elapsed.TotalSeconds, kind, dropped.PrinterId, window.Total);
    }

    public void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)
    {
        _channel.Writer.TryWrite(new TelemetryWriteItem.TelemetryItem(printerId, receivedAt, telemetry));
    }

    public void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)
    {
        _channel.Writer.TryWrite(new TelemetryWriteItem.EventItem(printerId, receivedAt, eventDto));
    }

    /// <inheritdoc />
    public TelemetryHealthSnapshot Current => _health;

    /// <inheritdoc />
    public bool IsDraining => ExecuteTask is null or { IsCompleted: false };

    /// <summary>
    /// Stops accepting work, then waits for the drain loop to finish what it already has.
    /// </summary>
    /// <remarks>
    /// Closing the writer is the whole shutdown signal: <see cref="ExecuteAsync"/>'s loop ends when
    /// the channel is completed <em>and</em> empty, so everything queued at this moment is processed
    /// and flushed first. Ordered before <c>base.StopAsync</c> deliberately - that is what cancels
    /// <c>stoppingToken</c> and then awaits the loop, so the channel has to be closed before the wait
    /// begins or the loop would have no reason to end. <see cref="Enqueue(int,DateTimeOffset,TelemetryDTO)"/>
    /// silently no-ops after this point, which is correct: a socket handler mid-message during
    /// shutdown has nowhere to put its data anyway.
    /// </remarks>
    /// <summary>Set by <see cref="ExecuteAsync"/>'s first statement; awaited by
    /// <see cref="StartAsync"/> so that "started" means the loop is actually running.</summary>
    private readonly TaskCompletionSource _executeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Starts the drain loop, and does not return until it is genuinely running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On .NET 10, <see cref="BackgroundService.StartAsync"/> schedules <see cref="ExecuteAsync"/>
    /// onto the thread pool instead of entering it synchronously - and <see cref="StopAsync"/>
    /// cancels the stopping token, which cancels a work item that has not started yet. So a stop
    /// racing a busy pool could cancel the loop <i>before its first line ran</i>: nothing drained,
    /// nothing logged, everything queued silently discarded, and
    /// <see cref="BackgroundService.StopAsync"/>'s WhenAny surfacing none of it. Reproduced 2000 out
    /// of 2000 under a saturated pool; in this project's test suite it was an intermittent one-in-
    /// ten "expected 25 rows, found 0" with an empty log.
    /// </para>
    /// <para>
    /// Waiting for the loop's own first statement closes that window for every caller that awaits
    /// StartAsync - the generic host does, and so do the tests. WhenAny with
    /// <see cref="BackgroundService.ExecuteTask"/> so a loop that dies before its first line (start
    /// token already cancelled) still lets startup complete rather than hang.
    /// </para>
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        await Task.WhenAny(_executeEntered.Task, ExecuteTask ?? Task.CompletedTask);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _executeEntered.TrySetResult();

        Dictionary<int, LiveStateCacheEntry> cache = new();
        List<TelemetrySample> pendingSamples = [];
        List<PrinterEvent> pendingEvents = [];
        HashSet<int> dirtyPrinterIds = [];

        using PeriodicTimer flushTimer = new(TimeSpan.FromSeconds(Math.Max(_options.WriteFlushIntervalSeconds, 0.05)));

        // Kept alive across loop iterations and only replaced once it actually fires - recreating it
        // on every pass through the outer loop would mean a busy printer's steady stream of channel
        // reads starves the timer branch forever, and low-traffic printers would never flush on
        // schedule.
        Task<bool> timerTick = flushTimer.WaitForNextTickAsync().AsTask();

        // Neither branch is cancellation-driven, and stoppingToken is deliberately never passed into
        // the work below - see StopAsync and the class remarks. Both awaited tasks simply report a
        // bool: the channel says "readable" or "completed and empty", the timer says "tick". So there
        // is no OperationCanceledException to catch, no exception filter to get right, and no way for
        // shutdown to land in the middle of processing an item.
        while (true)
        {
            Task<bool> channelReadable = _channel.Reader.WaitToReadAsync().AsTask();

            Task<bool> completed = await Task.WhenAny(channelReadable, timerTick);

            if (completed == channelReadable)
            {
                if (!await channelReadable)
                {
                    // Completed and drained: StopAsync closed the writer and every queued item has
                    // been processed. The only loop exit.
                    break;
                }

                while (_channel.Reader.TryRead(out TelemetryWriteItem? item))
                {
                    await ProcessItemAsync(item, cache, pendingSamples, pendingEvents, dirtyPrinterIds, CancellationToken.None);

                    if (pendingSamples.Count + pendingEvents.Count >= _options.WriteBatchSize)
                    {
                        await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, CancellationToken.None);
                    }
                }
            }
            else
            {
                await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, CancellationToken.None);

                timerTick = flushTimer.WaitForNextTickAsync().AsTask();
            }
        }

        // Whatever the last partial batch left buffered. Reached only via the break above, so the
        // channel is already empty - this is about the in-memory buffers, nothing else.
        //
        // Retried, unlike every flush before it: see FinalFlushAttempts. SafeFlushAsync leaves the
        // buffers populated when it fails and clears them when it succeeds, so their emptiness is
        // the success signal.
        for (int attempt = 1; attempt <= FinalFlushAttempts; attempt++)
        {
            await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, CancellationToken.None);

            if (pendingSamples.Count == 0 && pendingEvents.Count == 0)
            {
                break;
            }

            if (attempt < FinalFlushAttempts)
            {
                _logger.LogWarning(
                    "Telemetry flush failed during shutdown (attempt {Attempt} of {Attempts}); retrying before giving up on {SampleCount} samples and {EventCount} events.",
                    attempt, FinalFlushAttempts, pendingSamples.Count, pendingEvents.Count);

                await Task.Delay(FinalFlushRetryDelay);
            }
        }

        // The only place that can honestly report whether shutdown saved everything, and the signal
        // an operator waiting out the drain is looking for. SafeFlushAsync leaves the buffers
        // populated when a flush fails, so anything still here is about to die with the process -
        // that is a Warning, not a silent exit.
        if (pendingSamples.Count > 0 || pendingEvents.Count > 0)
        {
            _logger.LogWarning(
                "Telemetry drain finished with {SampleCount} samples and {EventCount} events unwritten; they are lost with this process.",
                pendingSamples.Count, pendingEvents.Count);
        }
        else
        {
            _logger.LogInformation("Telemetry drained to the database. Shutdown can complete safely.");
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
            // Throttled like the drop warning, and for a sharper reason: a normal printer never
            // sends unprocessable messages, but an attacker can stream them at wire rate - and this
            // site logs a full stack trace per item, which is the heaviest log entry in the class.
            // The skip semantics are untouched; only the logging is capped.
            if (_processingFailureWarnings.Record() is { } window)
            {
                if (window.IsFirstOccurrence)
                {
                    _logger.LogError(e, "[{PrinterId}] dropping one message that failed to process.", item.PrinterId);
                }
                else
                {
                    _logger.LogError(e,
                        "[{PrinterId}] dropping one message that failed to process - {Count} such failure(s) in the last {ElapsedSeconds:F0}s, {Total} since startup. The latest failure's exception is attached.",
                        item.PrinterId, window.Count, window.Elapsed.TotalSeconds, window.Total);
                }
            }
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

            TrimExcessPendingSamples(pendingSamples);
        }
    }

    /// <summary>
    /// Caps how large the buffer can grow while flushes keep failing (<see cref="SafeFlushAsync"/>
    /// deliberately leaves it populated for the next attempt), discarding the oldest samples first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only ever called with <c>pendingSamples</c>.</b> Events have their own, deliberately
    /// distant ceiling in <see cref="TrimExcessPendingEvents"/> - a stuck database should cost
    /// history density long before it costs a permanently missing, never-repeated event. That
    /// asymmetry is enforced by each method's signature accepting only one buffer, not by a runtime
    /// check.
    /// </para>
    /// <para>
    /// Without this, a database that is down or locked for an extended period - not just briefly
    /// behind, which the channel's own headroom already covers - would let <c>pendingSamples</c>
    /// grow without any ceiling, one dense telemetry message at a time, for as long as the outage
    /// lasts.
    /// </para>
    /// </remarks>
    private void TrimExcessPendingSamples(List<TelemetrySample> pendingSamples)
    {
        int cap = Math.Max(_options.WriteBatchSize, 1) * MaxPendingSampleBatches;

        if (pendingSamples.Count <= cap)
        {
            return;
        }

        int excess = pendingSamples.Count - cap;
        pendingSamples.RemoveRange(0, excess);

        _logger.LogWarning(
            "Discarded {Count} buffered telemetry samples to cap memory - the database has been failing to accept flushes for a while. Events are held far longer than this.",
            excess);
    }

    /// <summary>
    /// The same ceiling for events, reached only long after samples have started giving way.
    /// </summary>
    /// <remarks>
    /// Events are the last thing to drop, but "last" cannot mean "never": an unbounded buffer in a
    /// service that runs for months ends in the process dying, which loses every event this was
    /// protecting <i>and</i> the samples. Shedding the oldest events loses strictly less than
    /// eventually losing all of them.
    ///
    /// Logged at Error, where the sample trim logs Warning: thinning history is degradation,
    /// discarding an event is data loss with nothing to reconstruct it from.
    /// </remarks>
    private void TrimExcessPendingEvents(List<PrinterEvent> pendingEvents)
    {
        int cap = Math.Max(_options.WriteBatchSize, 1) * MaxPendingEventBatches;

        if (pendingEvents.Count <= cap)
        {
            return;
        }

        int excess = pendingEvents.Count - cap;
        pendingEvents.RemoveRange(0, excess);
        _discardedEvents += excess;

        _logger.LogError(
            "Discarded {Count} buffered printer events to cap memory - the database has been rejecting flushes long enough to exhaust even the event buffer. These events are lost.",
            excess);
    }

    private void ProcessEvent(TelemetryWriteItem.EventItem item, List<PrinterEvent> pendingEvents)
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
            Payload = FormatPayload(dto.Data),
        });

        TrimExcessPendingEvents(pendingEvents);
    }

    private async Task<LiveStateCacheEntry> HydrateAsync(int printerId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();

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

            _consecutiveFlushFailures = 0;
            _lastFlushAt = TimeProvider.System.GetUtcNow();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _consecutiveFlushFailures++;

            _logger.LogError(e, "Telemetry flush failed; {SampleCount} samples and {EventCount} events remain pending for the next attempt.",
                pendingSamples.Count, pendingEvents.Count);
        }
        finally
        {
            PublishHealth(pendingSamples.Count, pendingEvents.Count);
        }
    }

    /// <summary>
    /// Republishes the health snapshot after a flush attempt.
    /// </summary>
    /// <remarks>
    /// A whole record swapped by reference, rather than individually readable fields: the drain loop
    /// writes these while a request thread reads them, and a single reference assignment is atomic
    /// where a multi-field struct read is not. A health check should never be able to see half of one
    /// flush and half of the next.
    /// </remarks>
    private void PublishHealth(int pendingSamples, int pendingEvents) =>
        _health = new TelemetryHealthSnapshot(_lastFlushAt, _consecutiveFlushFailures, pendingSamples, pendingEvents,
                                              _dropWarnings.Total, _discardedEvents);

    /// <summary>
    /// Writes everything buffered since the last flush in one transaction, then clears the buffers.
    /// A no-op if nothing is pending, which the timer branch hits constantly on an idle deployment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Explicit per-entity <see cref="EntityState"/>, not <c>Update()</c>/<c>Add()</c>.</b> Those
    /// convenience methods decide Added-vs-Modified from whether an entity's key looks like a CLR
    /// default - which is useless here, since <see cref="PrinterLiveState"/> and
    /// <see cref="PrinterLiveSlotState"/> both key on real, non-default printer/slot numbers whether
    /// or not a row has ever been saved. Guessing wrong for a slot reported for the first time mid
    /// -life would silently issue an <c>UPDATE</c> that matches zero rows rather than an
    /// <c>INSERT</c> - no exception, just a permanently missing slot. <see cref="LiveStateCacheEntry"/>
    /// already knows exactly which rows exist, from <see cref="HydrateAsync"/> and from every prior
    /// flush, so it is used directly instead of asking EF to infer it.
    /// </para>
    /// <para>
    /// <b>"One transaction" is literal, including <c>Printer.LoadedMaterial</c>.</b> That update is a
    /// tracked single-property change on an attached, unloaded stub - not
    /// <c>ExecuteUpdateAsync</c>, which runs as its own immediate statement outside
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>'s implicit transaction and would
    /// commit on its own even if the save below then failed. Every write in this method rises or
    /// falls with that one call.
    /// </para>
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
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();

        if (pendingSamples.Count > 0)
        {
            context.TelemetrySamples.AddRange(pendingSamples);
        }

        if (pendingEvents.Count > 0)
        {
            context.PrinterEvents.AddRange(pendingEvents);
        }

        // Every cache mutation below is recorded here rather than applied directly, and only
        // carried out once SaveChangesAsync below actually succeeds. Applying any of them before
        // the save is confirmed would leave the cache believing something is true in the database
        // that a rolled-back save never actually wrote - permanently, for the rest of the process's
        // life, since nothing else ever corrects it:
        // - ExistsInDatabase: every later flush would choose Modified over Added and issue an
        //   UPDATE against a row that was never created, failing forever even once whatever caused
        //   the original failure is resolved.
        // - ExistingSlotNumbers: the same failure, per slot.
        // - PendingLoadedMaterial: clearing it here and having the save then fail would mean the
        //   material is never retried, yet nothing else remembers the printer still needs it.
        List<(LiveStateCacheEntry entry, List<int> newSlotNumbers, bool clearsPendingMaterial)> newlyPersisted = [];

        foreach (int printerId in dirtyPrinterIds)
        {
            LiveStateCacheEntry entry = cache[printerId];

            // Cleared before attaching, every time. The Printer stub below is attached into this same
            // context, and EF fixes up the relationship by writing it onto this navigation - onto the
            // cached entry, which outlives the context it came from. Left in place, the next flush
            // attaches that stale instance along with the live state and then collides with its own
            // fresh stub ("another instance with the same key value is already being tracked"),
            // failing every flush from then on. Nothing ever clears it, so the writer accepts
            // telemetry it can never persist for the rest of the process's life.
            //
            // The cache is meant to hold no context-bound state at all - see HydrateAsync, which is
            // careful to produce exactly that. This keeps it true after the first flush as well.
            entry.State.Printer = null;

            context.Attach(entry.State);
            context.Entry(entry.State).State = entry.ExistsInDatabase ? EntityState.Modified : EntityState.Added;

            List<int> newSlotNumbers = [];

            foreach (PrinterLiveSlotState slot in entry.State.Slots)
            {
                bool slotExists = entry.ExistingSlotNumbers.Contains(slot.SlotNumber);
                context.Entry(slot).State = slotExists ? EntityState.Modified : EntityState.Added;

                if (!slotExists)
                {
                    newSlotNumbers.Add(slot.SlotNumber);
                }
            }

            bool clearsPendingMaterial = false;

            if (entry.PendingLoadedMaterial is { } material)
            {
                // A tracked single-property update, not ExecuteUpdateAsync: that runs as its own
                // immediate statement against the database, independent of the SaveChangesAsync
                // below - so if the save later failed, this would already have committed on its
                // own, silently breaking the "one transaction" this method promises. Attaching an
                // unloaded stub and marking only LoadedMaterial as changed folds it into the same
                // SaveChangesAsync call as everything else, so it succeeds or rolls back with it.
                Printer printerStub = new() { Id = printerId };
                context.Attach(printerStub);
                printerStub.LoadedMaterial = material;
                context.Entry(printerStub).Property(p => p.LoadedMaterial).IsModified = true;

                clearsPendingMaterial = true;
            }

            newlyPersisted.Add((entry, newSlotNumbers, clearsPendingMaterial));
        }

        await context.SaveChangesAsync(cancellationToken);

        // Only reached once the save above has actually succeeded.
        foreach ((LiveStateCacheEntry entry, List<int> newSlotNumbers, bool clearsPendingMaterial) in newlyPersisted)
        {
            entry.ExistsInDatabase = true;

            foreach (int slotNumber in newSlotNumbers)
            {
                entry.ExistingSlotNumbers.Add(slotNumber);
            }

            if (clearsPendingMaterial)
            {
                entry.PendingLoadedMaterial = null;
            }
        }

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
