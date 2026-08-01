using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
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
    /// buffer fills far faster. Measured 2026-07-29: samples began giving way at +2.1 s and events at
    /// +15.5 s, 7.6x longer. Both buffers are shared across every connected printer, so more printers
    /// make each ceiling arrive sooner rather than making it larger.
    ///
    /// <b>This ordering governs the buffers only, and that is less than it sounds.</b> The intake
    /// channel in front of them is <see cref="BoundedChannelFullMode.DropOldest"/> and sheds events
    /// and telemetry alike, so an event has to survive the channel before any of this protects it.
    /// Measured 2026-07-30 with events pinned to one per 2 s through a 20 s blast: roughly ten
    /// emitted, three persisted, against a 96.7% drop rate. "Events are the last thing to give way"
    /// was stated here as though it were end to end; it is not. Open question, not a defect to fix
    /// in passing - see backlog.md, "Event loss under saturation".
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
    // Two, down from three (2026-07-30): each attempt is genuinely bounded now, so the budget
    // arithmetic in MaxShutdownFlushDuration is real - and at three bounded attempts plus the
    // in-flight flush the total no longer fitted the container's stop grace. Two patient attempts
    // beat three that get the process SIGKILLed before the loss is even reported.
    private const int FinalFlushAttempts = 2;

    /// <summary>
    /// What replaces a redacted value - a value, not an absence, so the redaction is legible.
    /// </summary>
    private const string RedactedMarker = "[redacted]";

    /// <summary>
    /// Every field a <c>FILE_INFO</c>'s <c>data</c> carries that <b>firmware itself renders</b>. Read
    /// straight off render.cpp:466-498 at the pinned ref, where the branch is six fixed fields plus
    /// one variant chunk - and the variant's only non-gcode alternative is the directory listing,
    /// hence <c>children</c> and <c>file_count</c>.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a blacklist, and the asymmetry is the whole argument - see
    /// <see cref="FormatPayload"/>. Note <c>preview</c> is firmware-rendered and still absent: it is
    /// the one field firmware produces that is pure gcode content.
    /// </remarks>
    private static readonly string[] FirmwareRenderedFileInfoFields =
        ["size", "m_timestamp", "read_only", "display_name", "type", "path", "children", "file_count"];

    /// <summary>
    /// Every <c>INFO</c> field masked before the payload reaches a row, as a dotted path from the
    /// object's root. See <see cref="RedactCredentials"/> for why this is a blacklist where its
    /// neighbour above is an allowlist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paths, not bare names</b>, so a same-named field elsewhere in the object is not redacted by
    /// accident - and because two of these are nested and a flat match would never have reached them.
    /// </para>
    /// <para>
    /// <b><c>api_key</c> is the credential</b>: the printer's PrusaLink password, which with the
    /// address beside it grants full authenticated access to its HTTP API. That one is the security
    /// fix. The other two are privacy, and the argument is different.
    /// </para>
    /// <para>
    /// <b>The wifi fields are here because an SSID can leak where someone lives</b> (Henrik,
    /// 2026-07-31). It is user-authored free text naming a home - often a surname or a street - and
    /// SSIDs are searchable in public wardriving databases in a way a printer serial is not. Nothing
    /// in this codebase reads either field; they were stored only because the payload was stored
    /// verbatim. The one real diagnostic use - spotting a printer that joined the wrong SSID on a
    /// multi-SSID network - is a question about <i>now</i>, answered by the current <c>INFO</c> or by
    /// walking up to the printer and reading its screen. This table is history, and history of it buys
    /// nothing.
    /// </para>
    /// <para>
    /// <b>Weighed and deliberately kept:</b> <c>sn</c> identifies the user's own device to the user
    /// and is what anyone would quote in a support conversation; <c>fingerprint</c> is how a
    /// connection is tied to a printer at all. Neither is a bearer credential, and both earn their
    /// place.
    /// </para>
    /// <para>
    /// <b>Limit worth knowing:</b> matching descends into nested <i>objects</i> but not into arrays,
    /// so a field inside <c>storages[]</c> could not be named here today. Nothing in <c>INFO</c> needs
    /// it; if that changes, <see cref="RedactCredentials"/> is where it changes.
    /// </para>
    /// </remarks>
    private static readonly string[] RedactedInfoFields =
        ["api_key", "network_info.wifi_ssid", "network_info.wifi_mac"];

    private static readonly TimeSpan FinalFlushRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// What one shutdown flush attempt may spend waiting on the database, ignoring
    /// <see cref="StorageOptions.BusyTimeoutMilliseconds"/>, which is sized for a running service.
    /// </summary>
    /// <remarks>
    /// A shutdown has a budget a running service does not, and it is set outside the process: the
    /// container runtime SIGKILLs after its grace period, so an attempt that would have succeeded at
    /// twelve seconds is not patient, it is simply killed - and killed mid-drain, which loses the
    /// buffers *and* the log line saying what was lost. Giving up sooner and reporting is strictly
    /// better than waiting longer and being terminated. See <see cref="MaxShutdownFlushDuration"/>
    /// for the arithmetic this feeds.
    /// </remarks>
    /// <remarks>
    /// Chosen from the outside in, not picked for feel. A 15 s stop grace has to cover three things,
    /// and the first is easy to forget: the flush the drain loop is *already inside* when SIGTERM
    /// arrives, which runs to the ordinary <see cref="StorageOptions.BusyTimeoutMilliseconds"/>
    /// budget before the loop can even see that it is shutting down. Then these attempts, then
    /// process teardown. Leaving that first term out is what left a measured shutdown at 11 s
    /// against an 11 s timeout - still killed, still no report. Deliberately as patient as the
    /// arithmetic allows, since the cost of giving up early is data a longer wait would have saved.
    /// </remarks>
    private static readonly TimeSpan FinalFlushCommandBudget = TimeSpan.FromMilliseconds(3000);

    private readonly Channel<TelemetryWriteItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _options;
    private readonly ILogger<TelemetryWriter> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UnknownFieldTracker _unknownFields;

    // Both wire-rate log sites in this class go through a LogThrottle: drops are recorded on
    // whatever producer thread hit the full channel, processing failures on the drain loop, and
    // either can arrive at wire rate (the second is attacker-driveable - a stream of deliberately
    // unprocessable messages). See LogThrottle's remarks for the blast-test numbers behind this.
    private readonly Services.LogThrottle _dropWarnings = new(TimeSpan.FromSeconds(10));
    private readonly Services.LogThrottle _processingFailureWarnings = new(TimeSpan.FromSeconds(10));

    // The two buffer-ceiling trims are wire-rate sites too, for a reason easy to miss: they are
    // called per item processed, so once a buffer is *at* its cap every further message trims
    // exactly one row and logs it. Measured during one 180 s outage: 50,007 sample-trim Warnings
    // and 5,193 event-trim Errors, every one of them reporting a count of 1.
    private readonly Services.LogThrottle _sampleTrims = new(TimeSpan.FromSeconds(10));
    private readonly Services.LogThrottle _eventTrims = new(TimeSpan.FromSeconds(10));

    // Written only by the drain loop, read by whatever calls Current (a health check, on a request
    // thread). Published as one immutable snapshot rather than read field by field - see PublishHealth.
    private volatile TelemetryHealthSnapshot _health = TelemetryHealthSnapshot.Initial;
    private DateTimeOffset? _lastFlushAt;
    private int _consecutiveFlushFailures;
    private volatile bool _shuttingDown;

    public TelemetryWriter(IServiceScopeFactory scopeFactory,
                           IOptions<StorageOptions> options,
                           ILogger<TelemetryWriter> logger,
                           TimeProvider timeProvider,
                           UnknownFieldTracker unknownFields)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _unknownFields = unknownFields;

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

    /// <summary>Same seam for the sample-buffer trim Warning. See <see cref="DropWarningInterval"/>.</summary>
    public TimeSpan SampleTrimWarningInterval
    {
        get => _sampleTrims.Interval;
        init => _sampleTrims.Interval = value;
    }

    /// <summary>Same seam for the event-buffer trim Error. See <see cref="DropWarningInterval"/>.</summary>
    public TimeSpan EventTrimWarningInterval
    {
        get => _eventTrims.Interval;
        init => _eventTrims.Interval = value;
    }

    /// <summary>
    /// The event's <c>data</c> as it goes into the row - verbatim, except that a <c>FILE_INFO</c> is
    /// reduced to <see cref="FirmwareRenderedFileInfoFields"/> and an <c>INFO</c> has its
    /// <c>api_key</c> masked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything else in a <c>FILE_INFO</c> is the uploaded gcode's own header</b>, relayed by
    /// firmware rather than produced by it. Measured on a real transfer: of 407 keys, 7 were
    /// firmware-rendered, <b>396 appeared verbatim in the gcode we already store</b>, and the
    /// remaining 4 were base64 thumbnail fragments the firmware's <c>key = value</c> split mangled
    /// out of that same file - because base64 padding is <c>=</c>. Nothing was unaccounted for. An
    /// event log should not carry a second copy of a file we hold, and a view that needs this wants
    /// it from somewhere built to serve it (Henrik, 2026-07-27).
    /// </para>
    /// <para>
    /// <b>Why an allowlist rather than a blacklist</b>, which was weighed and rejected: the two sets
    /// have different shapes. Firmware's is <i>closed</i> - render.cpp's FileInfo branch is six fixed
    /// fields plus one variant chunk, with no other producer, so it can be enumerated exactly from
    /// source. The gcode's is <i>unbounded and attacker-influenced</i>: 400 keys from one ordinary
    /// six-cube print, four of them unpredictable base64 strings that no blacklist could have named in
    /// advance. A crafted file chooses its own key names and count.
    /// </para>
    /// <para>
    /// The residual risk is real and deliberately accepted: <b>a future firmware field would be
    /// dropped silently</b>, because a new firmware field and a new gcode header arrive
    /// indistinguishably. It is bounded - the wire contract is verified stable across 6.5.7 to 6.6.3 -
    /// and recoverable, since a <c>SEND_FILE_INFO</c> re-requests the full object and the fix is one
    /// string in the list above. The blacklist's failure mode is not recoverable: unbounded content
    /// already written to a table that retention never sweeps. <b>When the pinned firmware ref moves,
    /// re-read render.cpp's FileInfo branch</b> - that check is the safeguard here, not anything in
    /// this code.
    /// </para>
    /// <para>
    /// Size, for scale: 3 x <c>FILE_INFO</c> per transferred file at ~18 KB each, none of them pruned,
    /// against ~493 bytes for all three under this rule. And the two large ones differed in exactly one
    /// field - <c>read_only</c> - so 17 538 bytes were being stored twice to record a boolean flipping.
    /// </para>
    /// </remarks>
    private static string? FormatPayload(EventDTO dto)
    {
        if (dto.Data is not { } element)
        {
            return null;
        }

        if (dto.EventType == Model.Events.Info && element.ValueKind == JsonValueKind.Object)
        {
            return RedactCredentials(element);
        }

        // Scoped to FILE_INFO deliberately. Every other event type has its own field set, and the
        // allowlist above describes this one only - applying it anywhere else would silently empty
        // payloads that are entirely firmware's own work.
        if (dto.EventType != Model.Events.FileInfo || element.ValueKind != JsonValueKind.Object)
        {
            return element.GetRawText();
        }

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (Array.IndexOf(FirmwareRenderedFileInfoFields, property.Name) >= 0)
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// An <c>INFO</c> payload with every <see cref="RedactedInfoFields"/> entry masked. Today that is
    /// <c>api_key</c> alone - <b>the printer's PrusaLink password</b>, which firmware volunteers in
    /// every one of these (<see cref="DTO.EventMessages.InfoEventDataDTO.ApiKey"/> says why it is a
    /// credential and what it unlocks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found in a live events table, 2026-07-31</b>, sitting in clear text beside the printer's
    /// address - which is the pair that grants full authenticated access to its HTTP API, including
    /// reading any file off the drive. Rotating the password on the printer does not remove earlier
    /// copies, and this table is append-only with no retention sweep, so every reconnection since
    /// enrolment had left one.
    /// </para>
    /// <para>
    /// <b>A blacklist here, and an allowlist for <c>FILE_INFO</c> - deliberately opposite</b>, because
    /// the risks are opposite. That one guards against <i>unbounded, attacker-influenced</i> gcode
    /// headers, where nothing can be enumerated in advance. This one guards named fields in a small,
    /// closed, firmware-rendered object that <see cref="UnknownFieldTracker"/> exists to watch for
    /// changes - and an allowlist would silently drop the new firmware fields that tracker is meant to
    /// surface. The residual risk is a *future* credential field arriving unnamed; that is what
    /// re-reading render.cpp at a new pinned ref is for.
    /// </para>
    /// <para>
    /// <b>Masked rather than dropped</b>, so the field's presence stays visible. A silently absent key
    /// reads as "firmware stopped sending it" to whoever looks next, which is the sort of ambiguity
    /// that costs an afternoon.
    /// </para>
    /// <para>
    /// <b>This does not scrub rows already written.</b> Existing payloads keep whatever key was
    /// current when they were stored; a deployment that cares needs to clear them out of band. Said
    /// plainly here rather than left for someone to discover.
    /// </para>
    /// </remarks>
    private static string RedactCredentials(JsonElement element)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteRedacted(writer, element, prefix: null);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Copies one object through, masking any property whose dotted path appears in
    /// <see cref="RedactedInfoFields"/>, and recursing so nested ones are reachable.
    /// </summary>
    /// <param name="writer">Where the copy goes.</param>
    /// <param name="element">The object being copied. Must be <see cref="JsonValueKind.Object"/>.</param>
    /// <param name="prefix">
    /// The dotted path of <paramref name="element"/> itself, or null at the root.
    /// </param>
    /// <remarks>
    /// <b>Objects only, not arrays.</b> An array is copied whole, so a field inside one cannot be
    /// named in the list. Nothing in <c>INFO</c> needs it - <c>storages</c> carries mount points and
    /// free space - and descending into arrays would need an index or a wildcard in the path syntax,
    /// which is more machinery than a list of three earns. The limit is stated on the list too, since
    /// that is where someone will be looking when it matters.
    /// </remarks>
    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element, string? prefix)
    {
        writer.WriteStartObject();

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string path = prefix is null ? property.Name : $"{prefix}.{property.Name}";

            if (Array.IndexOf(RedactedInfoFields, path) >= 0)
            {
                writer.WriteString(property.Name, RedactedMarker);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName(property.Name);
                WriteRedacted(writer, property.Value, path);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
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

    /// <summary>
    /// Worst case wall-clock time the shutdown drain can spend on its final flush: every attempt
    /// timing out, plus the delays between them.
    /// </summary>
    /// <remarks>
    /// Public because it is one end of a chain that spans three files and used to be settled in none
    /// of them: this budget must fit inside <c>HostOptions.ShutdownTimeout</c> (set from it in
    /// <c>Program.cs</c>), which must in turn fit inside the container runtime's stop grace period
    /// (<c>compose.yaml</c>). Before 2026-07-30 the middle value was the framework default of 30 s
    /// and the outer one Docker's default of 10 s, so the inner budget - three attempts that could
    /// each block ~10 s - overran both, and every shutdown against a stuck database was SIGKILLed
    /// with nothing logged about what it lost. <c>TelemetryWriterShutdownBudgetTests</c> pins the
    /// ordering so a future edit to the attempt count cannot quietly break it again.
    /// </remarks>
    public static TimeSpan MaxShutdownFlushDuration =>
        (FinalFlushCommandBudget * FinalFlushAttempts) + (FinalFlushRetryDelay * (FinalFlushAttempts - 1));

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
        // Set before the writer end closes, so the drain loop sees it for every item it still has.
        _shuttingDown = true;
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

        // Identity learned from INFO events, applied to the Printer row at flush time. Keyed by
        // printer, so a reconnecting printer that sends INFO twice before a flush costs one update.
        Dictionary<int, InfoEventDataDTO> pendingPrinterInfo = [];

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
                    await ProcessItemAsync(item, cache, pendingSamples, pendingEvents, dirtyPrinterIds, pendingPrinterInfo, CancellationToken.None);

                    // The failure guard is what stops a batch trigger becoming a per-message one.
                    // While flushes are failing the buffers never fall back below WriteBatchSize, so
                    // without it this condition holds for every item from then on: one full flush
                    // attempt, one exception and one Error-with-stack per message ingested - plus
                    // two more Errors from EF's own internals, ~8.9 KB of log per attempt, measured
                    // at 22 attempts/s during a 14 s outage (tools/slow-db). That is the same
                    // self-feeding shape the drop warning had, arriving by a different route: the
                    // log I/O steals the capacity the writer is already short of, and the retry
                    // itself is pure waste, since nothing has changed since the attempt one message
                    // ago. Retries fall back to the flush timer, which is the one clock that
                    // actually corresponds to "the database may have recovered by now".
                    //
                    // Bounded recovery: one success resets the counter and normal batch flushing
                    // resumes, so this costs at most WriteFlushIntervalSeconds of extra latency
                    // after an outage clears. It also fixes shutdown, where draining a full channel
                    // at one failed flush per item overran HostOptions.ShutdownTimeout and lost the
                    // buffers - the final flush after the loop is unaffected and still retries.
                    // Not while shutting down, whatever the buffers look like. The guard above keys
                    // off *failures*, and a blocked database produces none - so a lock left every
                    // drained item triggering its own multi-second attempt, and the drain used the
                    // whole shutdown budget before the final flush could start. Measured: 11 s and
                    // killed, with the loss summary never reached. Draining to memory and writing
                    // once at the end is both faster and the only version with a bounded cost.
                    if (pendingSamples.Count + pendingEvents.Count >= _options.WriteBatchSize
                        && _consecutiveFlushFailures == 0
                        && !_shuttingDown)
                    {
                        await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, pendingPrinterInfo, CancellationToken.None);
                    }
                }
            }
            else
            {
                await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, pendingPrinterInfo, CancellationToken.None);

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
            await SafeFlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, pendingPrinterInfo,
                                 CancellationToken.None, FinalFlushCommandBudget);

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
                                        Dictionary<int, InfoEventDataDTO> pendingPrinterInfo,
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
                    ProcessEvent(eventItem, pendingEvents, pendingPrinterInfo);
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

        // Throttled, because this runs per sample processed: at the cap, excess is 1 every time and
        // the old unthrottled line produced 50,007 entries in one 180 s outage, each announcing the
        // discarding of a single row. The window summary is the aggregate the message was always
        // phrased for. Same arrangement as the drop warning - occurrences counted exactly, only the
        // logging bounded.
        if (_sampleTrims.Record(excess) is not { } window)
        {
            return;
        }

        if (window.IsFirstOccurrence)
        {
            _logger.LogWarning(
                "Discarded {Count} buffered telemetry sample(s) to cap memory - the database has been failing to accept flushes for a while. Events are held far longer than this.",
                excess);

            return;
        }

        _logger.LogWarning(
            "Discarded {Count} buffered telemetry sample(s) in the last {ElapsedSeconds:F0}s to cap memory - the database is still not accepting flushes; {TotalDiscarded} samples discarded since startup. Events are held far longer than this.",
            window.Count, window.Elapsed.TotalSeconds, window.Total);
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

        // Throttled like the sample trim, and for the same per-item reason - but the level stays
        // Error and the first occurrence still logs in full: exhausting the event buffer is data
        // loss with nothing to reconstruct it from, and an operator should hear about it the moment
        // it starts. What is bounded is the repetition, which reached 5,193 identical Errors in one
        // 180 s outage. The exact lifetime total stays unthrottled on the health snapshot.
        if (_eventTrims.Record(excess) is not { } window)
        {
            return;
        }

        if (window.IsFirstOccurrence)
        {
            _logger.LogError(
                "Discarded {Count} buffered printer event(s) to cap memory - the database has been rejecting flushes long enough to exhaust even the event buffer. These events are lost.",
                excess);

            return;
        }

        _logger.LogError(
            "Discarded {Count} buffered printer event(s) in the last {ElapsedSeconds:F0}s - the database is still rejecting flushes and the event buffer remains exhausted; {TotalDiscarded} events lost since startup.",
            window.Count, window.Elapsed.TotalSeconds, window.Total);
    }

    /// <summary>
    /// Writes what the latest <c>INFO</c> events said each printer is, onto the <see cref="Printer"/>
    /// rows, as part of the caller's batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Loaded and tracked rather than stubbed</b>, unlike the <c>LoadedMaterial</c> writeback below:
    /// the serial number is only filled in when it is missing, which needs the current value. EF still
    /// writes only what changed, and it joins the same <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// as everything else, so it cannot commit on its own.
    /// </para>
    /// <para>
    /// <b>Firmware and model are overwritten; the serial number is not.</b> Firmware changes on every
    /// upgrade, and the model genuinely can change - Prusa sell upgrade kits, so an MK3 becomes an
    /// MK3.5 under the same identity. A serial number changing means a different printer, which would
    /// arrive with a different fingerprint and therefore be a different row, so a differing value here
    /// is not something to act on (Henrik, 2026-07-28). Absent fields are left alone rather than
    /// nulled - a field the firmware omits is unknown, not empty.
    /// </para>
    /// <para>
    /// <c>UpdatedAt</c> is deliberately not touched. It means "a person edited this printer", and a
    /// firmware upgrade reported by the hardware is not that.
    /// </para>
    /// </remarks>
    private async Task ApplyPrinterInfoAsync(HSDbContext context,
                                             Dictionary<int, InfoEventDataDTO> pendingPrinterInfo,
                                             CancellationToken cancellationToken)
    {
        // Which printers already have a serial, read untracked and projected to two columns. It has
        // to be a query rather than a look at the attached stub: a stub carries nulls for everything
        // it was not told about, so it cannot distinguish "no serial stored" from "not loaded".
        // Untracked and projected, deliberately - loading whole Printer entities tracked is what
        // collided with the live-state attach and failed every flush thereafter.
        Dictionary<int, string?> storedSerials = [];

        if (pendingPrinterInfo.Any(p => !string.IsNullOrWhiteSpace(p.Value.SerialNumber)))
        {
            List<int> printerIds = [.. pendingPrinterInfo.Keys];

            storedSerials = await context.Printers
                .AsNoTracking()
                .Where(p => printerIds.Contains(p.Id))
                .Select(p => new { p.Id, p.SerialNumber })
                .ToDictionaryAsync(p => p.Id, p => p.SerialNumber, cancellationToken);
        }

        foreach ((int printerId, InfoEventDataDTO info) in pendingPrinterInfo)
        {
            bool hasFirmware = !string.IsNullOrWhiteSpace(info.Firmware);
            bool hasModel = !string.IsNullOrWhiteSpace(info.PrinterType);
            bool hasNozzle = info.NozzleDiameter is > 0;
            bool hasMmuBlock = info.Mmu is not null;

            bool fillsSerial = !string.IsNullOrWhiteSpace(info.SerialNumber)
                               && string.IsNullOrWhiteSpace(storedSerials.GetValueOrDefault(printerId));

            if (!hasFirmware && !hasModel && !hasNozzle && !hasMmuBlock && !fillsSerial)
            {
                continue;
            }

            // Reuse the stub the material writeback may already have attached for this printer.
            // Attaching a second instance with the same key throws, and this class's failure mode for
            // that is permanent: the flush raises, the buffers are deliberately kept for a retry, and
            // every retry hits the same collision - so the writer accepts telemetry it can never
            // persist for the rest of the process's life.
            Printer printer = context.Printers.Local.FirstOrDefault(p => p.Id == printerId)
                              ?? Attach(context, printerId);

            if (hasFirmware)
            {
                printer.Firmware = info.Firmware;
                context.Entry(printer).Property(p => p.Firmware).IsModified = true;
            }

            if (hasModel)
            {
                printer.Model = info.PrinterType;
                context.Entry(printer).Property(p => p.Model).IsModified = true;
            }

            // Refreshed rather than filled in once: a nozzle swap is a change to the hardware as it
            // stands, not to which machine this is. Zero is treated as absent - a printer that has
            // not reported one sends no value, and a literal 0.0 mm nozzle does not exist.
            if (hasNozzle)
            {
                printer.NozzleDiameter = info.NozzleDiameter;
                context.Entry(printer).Property(p => p.NozzleDiameter).IsModified = true;
            }

            // Only when the block is present: absent means firmware without MMU support, which the
            // column's false default already says. Writing false here instead would let a partial
            // INFO clear a genuine true.
            if (hasMmuBlock)
            {
                printer.HasMmuEnabled = info.Mmu!.Enabled;
                context.Entry(printer).Property(p => p.HasMmuEnabled).IsModified = true;
            }

            if (fillsSerial)
            {
                printer.SerialNumber = info.SerialNumber;
                context.Entry(printer).Property(p => p.SerialNumber).IsModified = true;
            }
        }

        static Printer Attach(HSDbContext context, int printerId)
        {
            Printer stub = new() { Id = printerId };
            context.Attach(stub);

            return stub;
        }
    }

    private void ProcessEvent(TelemetryWriteItem.EventItem item,
                              List<PrinterEvent> pendingEvents,
                              Dictionary<int, InfoEventDataDTO> pendingPrinterInfo)
    {
        EventDTO dto = item.Data;

        if (dto.EventType == Model.Events.Info && dto.Data is { } data)
        {
            // INFO is the only message carrying what the printer *is* rather than what it is doing,
            // and it arrives on connection. Recorded here and applied at flush time, so it commits
            // with everything else in the batch - see FlushAsync.
            try
            {
                if (data.Deserialize<InfoEventDataDTO>() is { } info)
                {
                    // The one unknown-field site outside MessageDispatcher, because this is the one
                    // place an event's typed data is read at all. INFO's key set is firmware-rendered
                    // and closed, unlike the FILE_INFO payload two methods down - which is why that
                    // one gets an allowlist and this one gets noticed.
                    _unknownFields.Record(item.PrinterId, "event:Info.data", info.Unknown);

                    pendingPrinterInfo[item.PrinterId] = info;
                }
            }
            catch (JsonException e)
            {
                // Off the wire and attacker-shaped, so a malformed INFO must not take the drain loop
                // down. The event row itself is still written below, payload and all.
                _logger.LogWarning(e, "Printer {PrinterId} sent an INFO event whose data could not be read.", item.PrinterId);
            }
        }

        pendingEvents.Add(new PrinterEvent
        {
            PrinterId = item.PrinterId,
            Timestamp = item.ReceivedAt,
            EventType = dto.EventType,
            Status = PrinterStatusExtensions.ParseWireState(dto.Status),
            JobId = dto.JobId,
            CommandId = dto.CommandId,
            Reason = dto.Reason,
            Payload = FormatPayload(dto),
        });

        TrimExcessPendingEvents(pendingEvents);
    }

    /// <summary>
    /// Caps how long one of this writer's database commands may wait, from
    /// <see cref="StorageOptions.BusyTimeoutMilliseconds"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, that option does not mean what it says.</b> It documents itself as how long a
    /// blocked writer waits for the lock before failing, and
    /// <see cref="Homespool.Data.SqlitePragmaInterceptor"/> duly issues <c>PRAGMA busy_timeout</c> -
    /// but Microsoft.Data.Sqlite catches the resulting <c>SQLITE_BUSY</c> and retries it internally
    /// until <c>CommandTimeout</c> elapses, which defaults to 30 seconds. Measured with an outside
    /// connection holding <c>BEGIN IMMEDIATE</c>: a flush blocked for 30 s against a configured
    /// 5,000 ms, six times the documented value (tools/slow-db, <c>MECHANISM=lock</c>).
    /// </para>
    /// <para>
    /// <b>The point is not the wait, it is that blocking is invisible.</b> A blocked flush is not a
    /// failed one: <c>_consecutiveFlushFailures</c> stays 0, so the retry guard never engages, the
    /// health check - which grades on failures - goes on reporting Healthy, and a shutdown is killed
    /// mid-drain with no summary. Failing at the configured timeout instead puts lock contention
    /// back into the failure path that everything else in this class already handles properly.
    /// </para>
    /// <para>
    /// <b>Scoped to this writer's own contexts</b>, matching the option's wording, rather than set on
    /// the connection for the whole app: an API read that today waits out brief contention should not
    /// start failing as a 500 because the *writer* wants a shorter leash.
    /// </para>
    /// <para>
    /// <b>Never passed through as zero.</b> ADO.NET reads <c>CommandTimeout = 0</c> as "wait
    /// forever", the exact inverse of <c>busy_timeout = 0</c>'s "fail immediately", so a
    /// configuration meaning the most impatient possible writer would produce the most patient
    /// possible one. Rounded up to a whole second, floored at one.
    /// </para>
    /// </remarks>
    /// <param name="context">The context whose commands are being bounded.</param>
    /// <param name="budget">
    /// The wait to allow, or null for <see cref="StorageOptions.BusyTimeoutMilliseconds"/>. The
    /// shutdown flush passes <see cref="FinalFlushCommandBudget"/> instead, because its deadline
    /// comes from the container runtime rather than from configuration.
    /// </param>
    /// <param name="cancellationToken">Cancels the pragma statement, when one is issued.</param>
    // CA2100/EF1002: SQLite does not accept bound parameters in PRAGMA, so the value is
    // interpolated. It is an int computed from a TimeSpan this class owns - never user input.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "EF1002:Risk of vulnerability to SQL injection",
                                                     Justification = "PRAGMA cannot be parameterised; the interpolated value is an int this class computes.")]
    private async Task ApplyWriterCommandTimeoutAsync(HSDbContext context, TimeSpan? budget, CancellationToken cancellationToken)
    {
        TimeSpan effective = budget ?? TimeSpan.FromMilliseconds(Math.Max(_options.BusyTimeoutMilliseconds, 1));
        int seconds = Math.Max((int)Math.Ceiling(effective.TotalSeconds), 1);

        context.Database.SetCommandTimeout(seconds);

        // SetCommandTimeout alone does not bind, and finding that out cost three rig runs: it covers
        // the statements EF issues, but a flush's wait for the write lock happens at the transaction's
        // own BEGIN/COMMIT, and Microsoft.Data.Sqlite runs those against the *connection's*
        // DefaultTimeout - 30 s unless set. Measured: with only the command timeout wired, a flush
        // against a held lock blocked for the full 30 s whatever value was configured, and lowering
        // the connection string's "Default Timeout" was what finally moved it (the log always said
        // "An error occurred using a transaction", which was the clue). Set here on the writer's own
        // connection instance rather than in the connection string, so claims, Identity and the
        // retention sweep keep the patient default - a user write colliding with a long sweep should
        // wait it out, not turn into a 500 because the writer wanted a short leash.
        if (context.Database.GetDbConnection() is Microsoft.Data.Sqlite.SqliteConnection sqliteConnection)
        {
            sqliteConnection.DefaultTimeout = seconds;
        }

        if (budget is null)
        {
            // The connection already carries the pragma the interceptor set from the same option.
            return;
        }

        // A tighter budget has to move the pragma too. SQLite blocks for busy_timeout before it
        // reports SQLITE_BUSY at all, so a command can never return faster than the pragma however
        // low the timeouts above are set. Half the budget, so two waits still fit inside it.
        //
        // Opened explicitly first, and that is load-bearing: a pragma is per-connection, and EF
        // closes the connection after each command unless it was opened by the caller. Set on a
        // borrowed connection it is handed straight back to the pool, and the SaveChanges that
        // follows opens another one - where the interceptor re-applies the configured value and
        // quietly undoes this. The scope's disposal returns the connection either way.
        await context.Database.OpenConnectionAsync(cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            $"PRAGMA busy_timeout = {Math.Max((int)(effective.TotalMilliseconds / 2), 1)}", cancellationToken);
    }

    private async Task<LiveStateCacheEntry> HydrateAsync(int printerId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
        await ApplyWriterCommandTimeoutAsync(context, budget: null, cancellationToken);

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
                                      Dictionary<int, InfoEventDataDTO> pendingPrinterInfo,
                                      CancellationToken cancellationToken,
                                      TimeSpan? commandBudget = null)
    {
        try
        {
            await FlushAsync(cache, pendingSamples, pendingEvents, dirtyPrinterIds, pendingPrinterInfo, cancellationToken, commandBudget);

            _consecutiveFlushFailures = 0;
            _lastFlushAt = _timeProvider.GetUtcNow();
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
                                              _dropWarnings.Total, _eventTrims.Total);

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
                                  Dictionary<int, InfoEventDataDTO> pendingPrinterInfo,
                                  CancellationToken cancellationToken,
                                  TimeSpan? commandBudget = null)
    {
        if (pendingSamples.Count == 0 && pendingEvents.Count == 0 && dirtyPrinterIds.Count == 0)
        {
            return;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
        await ApplyWriterCommandTimeoutAsync(context, commandBudget, cancellationToken);

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

            // The cache holds no context-bound state - see HydrateAsync, which is careful to produce
            // exactly that, and PrinterLiveState's own remarks for why the entity has no Printer
            // navigation for EF's fix-up to violate that with. This used to be defended here by
            // nulling the navigation before every attach; the pending sample/event buffers had the
            // same flaw with no defence at all, and one failed flush carrying a material writeback
            // wedged persistence for the rest of the process's life (the slow-database rig,
            // 2026-07-29). The navigations are gone instead - all three types now cross contexts as
            // plain data, and TelemetryWriterTests pins the recovery.
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

        // After the loop above, so the material writeback's stub already exists and can be reused
        // rather than collided with.
        await ApplyPrinterInfoAsync(context, pendingPrinterInfo, cancellationToken);

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

        // Cleared only here, past the save, for the same reason as everything above it: a failed
        // flush leaves the pending identity in place so the next attempt still applies it. INFO
        // arrives once per connection, so dropping it on a failure would mean the printer's firmware
        // stayed wrong until it next reconnected.
        pendingPrinterInfo.Clear();
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
