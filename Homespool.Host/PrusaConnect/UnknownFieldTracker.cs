using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Microsoft.Extensions.Logging;

using Homespool.Host.Services;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Notices JSON properties a printer sent that this build does not model, so firmware saying
/// something new is visible instead of silent.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json discards unmatched properties by default, which made this the one wire failure
/// with no evidence anywhere: a printer could report something for months and leave no trace in a
/// log, a table or a metric. The DTOs now collect them through
/// <see cref="System.Text.Json.Serialization.JsonExtensionDataAttribute"/> and hand them here.
/// </para>
/// <para>
/// <b>Only the key name and its <see cref="JsonValueKind"/> are ever recorded - never the value.</b>
/// Commit <c>0a6ae53</c> removed uploaded gcode from the event log for the same reason, and an
/// unknown field's value is exactly as likely to be file content.
/// </para>
/// <para>
/// <b>Two bounds, because this path is reachable at wire rate by anyone who can connect.</b>
/// Distinct fields are capped at <see cref="MaxDistinctFields"/>, which bounds both memory and the
/// number of per-field warnings this can ever emit; past the cap a <see cref="LogThrottle"/> allows
/// one summary line per window. Occurrences are always counted exactly - see
/// <see cref="Total"/> - and only the logging is bounded, the same arrangement
/// <c>TelemetryHealthSnapshot.DroppedMessages</c> uses.
/// </para>
/// <para>
/// <b>What this deliberately does not see:</b> <see cref="DTO.EventMessages.EventDTO.Data"/> is a raw
/// <see cref="JsonElement"/> and is never walked. That is not an oversight - a single
/// <c>FILE_INFO</c> payload carried 407 keys, 396 of them lifted verbatim out of the uploaded gcode
/// with names the file chose (<c>8cf4cdb</c>). Unknown-field detection is meaningful precisely
/// because the envelope and telemetry key sets are closed and firmware-rendered; <c>data</c>'s is
/// neither, and is already stored verbatim by the writer. Callers enumerate the shapes they want
/// explicitly for this reason - there is no recursion to accidentally reach it.
/// </para>
/// </remarks>
public sealed class UnknownFieldTracker
{
    /// <summary>
    /// How many distinct <c>shape.field</c> names are remembered before the tracker stops learning
    /// new ones. Reaching it is itself a signal - a real firmware release adds a handful of fields,
    /// not sixty - so the cap doubles as the "something is generating junk" threshold.
    /// </summary>
    public const int MaxDistinctFields = 64;

    /// <summary>Spacing between "still seeing unknown fields past the cap" summaries.</summary>
    private static readonly TimeSpan CappedWarningInterval = TimeSpan.FromMinutes(5);

    private readonly LogThrottle _cappedWarnings = new(CappedWarningInterval);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ILogger<UnknownFieldTracker> _logger;

    private long _total;
    private int _distinct;

    public UnknownFieldTracker(ILogger<UnknownFieldTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>Exact number of unknown-field occurrences since this process started, unthrottled.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>
    /// The distinct <c>shape.field</c> names seen, at most <see cref="MaxDistinctFields"/> of them.
    /// Ordered, so a health payload does not reshuffle between polls for no reason.
    /// </summary>
    public IReadOnlyList<string> DistinctFields => _seen.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Records every unmatched property in <paramref name="unknown"/>, logging the first sighting of
    /// each distinct name.
    /// </summary>
    /// <param name="printerId">The printer that sent them, for the log line.</param>
    /// <param name="shape">
    /// What part of the wire this came from - <c>telemetry</c>, <c>telemetry.chamber</c>,
    /// <c>event:FILE_INFO</c>. Qualifying the field name by shape is what stops the same key on two
    /// different messages reading as one finding.
    /// </param>
    /// <param name="unknown">The extension-data dictionary, which is null when nothing was unmatched.</param>
    public void Record(int printerId, string shape, Dictionary<string, JsonElement>? unknown)
    {
        // Null is the overwhelmingly common case - a printer speaking the pinned protocol - and
        // System.Text.Json leaves the property alone rather than allocating an empty dictionary,
        // so the ordinary 1 Hz telemetry path costs this comparison and nothing else.
        if (unknown is null || unknown.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, JsonElement> field in unknown)
        {
            Interlocked.Increment(ref _total);

            // Volatile rather than a locked read of _seen.Count: ConcurrentDictionary.Count takes
            // every lock in the table, and this runs per field per message. The trade is that a
            // race can admit an entry or two beyond the cap, which costs two dictionary slots and
            // misleads nobody - the same "counts may smear, totals never do" bargain LogThrottle makes.
            if (Volatile.Read(ref _distinct) >= MaxDistinctFields)
            {
                if (_cappedWarnings.Record() is { } window)
                {
                    _logger.LogWarning(
                        "Unknown wire fields past the {Cap}-name cap; no longer learning new ones. {Count} occurrence(s) in the last {ElapsedSeconds:F0}s, {Total} since startup. Known names: {Fields}",
                        MaxDistinctFields, window.Count, window.Elapsed.TotalSeconds, window.Total, DistinctFields);
                }

                continue;
            }

            string name = $"{shape}.{field.Key}";

            if (_seen.TryAdd(name, 0))
            {
                Interlocked.Increment(ref _distinct);

                // Warning, not Information: this is the printer telling us something we are throwing
                // away, and the whole point is that it should be hard to miss. Bounded to
                // MaxDistinctFields lines for the life of the process, so it cannot become the
                // 722,973-line incident that produced LogThrottle.
                _logger.LogWarning(
                    "[{PrinterId}] unknown wire field {Field} ({Kind}) - the printer sent something this build does not model, and its value is being discarded.",
                    printerId, name, field.Value.ValueKind);
            }
        }
    }
}
