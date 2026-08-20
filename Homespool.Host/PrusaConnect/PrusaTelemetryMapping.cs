using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.Telemetry;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The Prusa Connect edge's conversion into the neutral telemetry currency - the merge-policy
/// sibling of <see cref="PrusaEventWireMapping"/>, and the one place this protocol's presence
/// semantics are stated. What used to be spread between the merger and the writer is three rules,
/// each now a visible choice of <see cref="Field{T}"/> spelling: most fields <b>coalesce</b> (the
/// wire omits what has not changed, so null maps to absent); the <b>job block clears as a unit</b>
/// when wholly absent, because firmware guards it with <c>if (params.has_job)</c> and absence is
/// its own boolean on the wire (the 99%-forever fix, <c>notes/print-queue.md</c>); and the
/// <b>chamber/enclosure/slot blocks are atomic</b> - firmware renders each whole or not at all, so
/// a present block overwrites every field in it.
/// </summary>
public static class PrusaTelemetryMapping
{
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
    /// fix. The other two are privacy: <b>an SSID can leak where someone lives</b> (Henrik,
    /// 2026-07-31) - user-authored free text naming a home, searchable in public wardriving
    /// databases in a way a printer serial is not. <c>sn</c> and <c>fingerprint</c> were weighed and
    /// deliberately kept: neither is a bearer credential, and both are what anyone quotes in a
    /// support conversation.
    /// </para>
    /// <para>
    /// <b>Limit worth knowing:</b> matching descends into nested <i>objects</i> but not into arrays,
    /// so a field inside <c>storages[]</c> could not be named here today. Nothing in <c>INFO</c> needs
    /// it; if that changes, <see cref="RedactCredentials"/> is where it changes.
    /// </para>
    /// </remarks>
    private static readonly string[] RedactedInfoFields =
        ["api_key", "network_info.wifi_ssid", "network_info.wifi_mac"];

    /// <summary>
    /// The neutral update one wire telemetry message amounts to - every presence decision this
    /// protocol makes, made here and nowhere downstream.
    /// </summary>
    public static TelemetryUpdate ToUpdate(TelemetryDTO telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // Firmware guards the whole block with if (params.has_job): the five fields arrive together
        // or not at all, and absence is firmware's own statement that there is no job. Keyed on the
        // whole block rather than any one field, so nothing depends on which firmware renders first.
        bool hasJob = telemetry.JobId is not null
                      || telemetry.Progress is not null
                      || telemetry.TimePrinting is not null
                      || telemetry.TimeRemaining is not null
                      || telemetry.TimeToFilamentChange is not null;

        TelemetryUpdate update = new()
        {
            Status = PrinterStatusExtensions.ParseWireState(telemetry.Status),

            JobId = hasJob ? telemetry.JobId : Field<int?>.Null,
            Progress = hasJob ? telemetry.Progress : Field<int?>.Null,
            TimePrinting = hasJob ? telemetry.TimePrinting : Field<int?>.Null,
            TimeRemaining = hasJob ? telemetry.TimeRemaining : Field<int?>.Null,
            TimeToFilamentChange = hasJob ? telemetry.TimeToFilamentChange : Field<int?>.Null,

            ExtruderFan = telemetry.ExtruderFan,
            PrintFan = telemetry.PrintFan,
            FilamentUsed = telemetry.FilamentUsed,
            NozzleTemperature = telemetry.NozzleTemperature,
            BedTemperature = telemetry.BedTemperature,
            TargetNozzleTemperature = telemetry.TargetNozzleTemperature,
            TargetBedTemperature = telemetry.TargetBedTemperature,
            Speed = telemetry.Speed,
            Flow = telemetry.Flow,
            Material = telemetry.Material,
            XAxis = telemetry.XAxis,
            YAxis = telemetry.YAxis,
            ZAxis = telemetry.ZAxis,
            HeatbreakTemperature = telemetry.HeatbreakTemperature,
            PsuTemperature = telemetry.PsuTemperature,
            AmbientTemperature = telemetry.AmbientTemperature,
            ExtruderFilamentSensorStatus = telemetry.ExtruderFilamentSensorStatus,
            RemoteFilamentSensorStatus = telemetry.RemoteFilamentSensorStatus,
        };

        if (telemetry.Chamber is { } chamber)
        {
            update = update with
            {
                ChamberTemperature = Field<float?>.Of(chamber.Temperature),
                ChamberTargetTemperature = Field<int?>.Of(chamber.TargetTemperature),
                ChamberFan1Rpm = Field<int?>.Of(chamber.Fan1Speed),
                ChamberFan2Rpm = Field<int?>.Of(chamber.Fan2Speed),
                ChamberFanPwmTarget = Field<int?>.Of(chamber.FanPwmTarget),
                ChamberLedIntensity = Field<int?>.Of(chamber.LedIntensity),
            };
        }

        if (telemetry.Enclosure is { } enclosure)
        {
            update = update with
            {
                EnclosureTemperature = Field<int?>.Of(enclosure.Temperature),
                EnclosureFanRpm = Field<int?>.Of(enclosure.FanSpeed),
                EnclosureTimeInUse = Field<int?>.Of(enclosure.TimeIsUse),
            };
        }

        if (telemetry.Slot is { } slot)
        {
            // Active is always present when the block is, but MmuState/MmuCommand are MMU-only - an
            // XL sends the block (tool changer, >1 tool) without ever populating those two, so they
            // coalesce rather than overwrite.
            update = update with
            {
                ActiveSlot = Field<int?>.Of(slot.Active),
                MmuState = slot.MmuState,
                MmuCommand = slot.MmuCommand,
                Slots = ToSlotUpdates(slot),
            };
        }

        return update;
    }

    /// <summary>
    /// The neutral record one wire event amounts to. <paramref name="identity"/> is the parsed
    /// <c>INFO</c> data where the dispatcher extracted one - parsed there rather than here because
    /// unknown-field accounting and the malformed-data log belong to the component that owns them.
    /// </summary>
    public static PrinterEventRecord ToRecord(EventDTO eventDto, PrinterIdentityUpdate? identity)
    {
        ArgumentNullException.ThrowIfNull(eventDto);

        return new PrinterEventRecord
        {
            EventType = eventDto.EventType,

            // Byte-identical to what arrived, because the table is bijective and an unknown word
            // already threw during parsing - see PrusaEventWireMapping's remarks.
            WireType = PrusaEventWireMapping.Format(eventDto.EventType),
            Status = PrinterStatusExtensions.ParseWireState(eventDto.Status),
            JobId = eventDto.JobId,
            CommandId = eventDto.CommandId,
            Reason = eventDto.Reason,
            Payload = FormatPayload(eventDto),
            Identity = identity,
        };
    }

    /// <summary>
    /// The identity update an <c>INFO</c>'s parsed data amounts to, with this wire's absence rules
    /// applied: whitespace is unreported, a zero nozzle is unreported (a literal 0.0 mm nozzle does
    /// not exist), and a missing MMU block is "firmware without MMU support", which must map to
    /// null rather than false so a partial <c>INFO</c> cannot clear a stored true.
    /// </summary>
    public static PrinterIdentityUpdate ToIdentity(InfoEventDataDTO info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new PrinterIdentityUpdate(
            string.IsNullOrWhiteSpace(info.Firmware) ? null : info.Firmware,
            string.IsNullOrWhiteSpace(info.PrinterType) ? null : info.PrinterType,
            info.NozzleDiameter is > 0 ? info.NozzleDiameter : null,
            info.Mmu?.Enabled,
            string.IsNullOrWhiteSpace(info.SerialNumber) ? null : info.SerialNumber,
            ToToolUpdates(info.Tools));
    }

    /// <summary>
    /// The per-tool hardware an <c>INFO</c>'s <c>tools</c> block amounts to, or null when it carried
    /// none - which must leave what is stored alone rather than clearing it.
    /// </summary>
    /// <remarks>
    /// <b>A tool whose key is not a number is skipped rather than failing the report.</b> The rest
    /// of an <c>INFO</c> is worth having even if one entry is nonsense, and this is a wire nobody
    /// here controls.
    /// </remarks>
    private static List<PrinterToolUpdate>? ToToolUpdates(Dictionary<string, InfoToolDTO>? tools)
    {
        if (tools is null)
        {
            return null;
        }

        List<PrinterToolUpdate> updates = [];

        foreach ((string key, InfoToolDTO tool) in tools)
        {
            if (!int.TryParse(key, CultureInfo.InvariantCulture, out int toolNumber))
            {
                continue;
            }

            // "---" is firmware's sentinel for no filament set, not a material name - see InfoToolDTO.
            string? material = string.IsNullOrWhiteSpace(tool.Material) || tool.Material == "---" ?
                null :
                tool.Material;

            updates.Add(new PrinterToolUpdate(toolNumber,
                                              tool.NozzleDiameter > 0 ? tool.NozzleDiameter : null,
                                              tool.Hardened,
                                              tool.HighFlow,
                                              material));
        }

        return updates;
    }

    private static List<SlotUpdate> ToSlotUpdates(SlotsTelemetryDTO slot)
    {
        List<SlotUpdate> updates = [];

        if (slot.Slots is null)
        {
            return updates;
        }

        foreach ((string key, JsonElement value) in slot.Slots)
        {
            int slotNumber = int.Parse(key, CultureInfo.InvariantCulture);
            ToolTelemetryDTO? tool = value.Deserialize<ToolTelemetryDTO>();

            if (tool is null)
            {
                continue;
            }

            updates.Add(new SlotUpdate(slotNumber, tool.Material, tool.Temperature, tool.HotendFan, tool.PrintFan));
        }

        return updates;
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
    /// out of that same file - because base64 padding is <c>=</c>. An event log should not carry a
    /// second copy of a file we hold (Henrik, 2026-07-27).
    /// </para>
    /// <para>
    /// <b>Why an allowlist rather than a blacklist</b>, which was weighed and rejected: the two sets
    /// have different shapes. Firmware's is <i>closed</i> - render.cpp's FileInfo branch is six fixed
    /// fields plus one variant chunk, with no other producer, so it can be enumerated exactly from
    /// source. The gcode's is <i>unbounded and attacker-influenced</i>: a crafted file chooses its
    /// own key names and count.
    /// </para>
    /// <para>
    /// The residual risk is real and deliberately accepted: <b>a future firmware field would be
    /// dropped silently</b>, because a new firmware field and a new gcode header arrive
    /// indistinguishably. It is bounded - the wire contract is verified stable across 6.5.7 to 6.6.3 -
    /// and recoverable, since a <c>SEND_FILE_INFO</c> re-requests the full object and the fix is one
    /// string in the list above. <b>When the pinned firmware ref moves, re-read render.cpp's FileInfo
    /// branch</b> - that check is the safeguard here, not anything in this code.
    /// </para>
    /// <para>
    /// Size, for scale: 3 x <c>FILE_INFO</c> per transferred file at ~18 KB each, none of them pruned,
    /// against ~493 bytes for all three under this rule - and the two large ones differed in exactly
    /// one field, <c>read_only</c>, so 17 538 bytes were being stored twice to record a boolean
    /// flipping.
    /// </para>
    /// </remarks>
    private static string? FormatPayload(EventDTO dto)
    {
        if (dto.Data is not { } element)
        {
            return null;
        }

        if (dto.EventType == Model.PrinterEventType.Info && element.ValueKind == JsonValueKind.Object)
        {
            return RedactCredentials(element);
        }

        // Scoped to FILE_INFO deliberately. Every other event type has its own field set, and the
        // allowlist above describes this one only - applying it anywhere else would silently empty
        // payloads that are entirely firmware's own work.
        if (dto.EventType != Model.PrinterEventType.FileInfo || element.ValueKind != JsonValueKind.Object)
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
    /// An <c>INFO</c> payload with every <see cref="RedactedInfoFields"/> entry masked -
    /// <c>api_key</c> is <b>the printer's PrusaLink password</b>, found sitting in clear text in a
    /// live events table on 2026-07-31, one copy per reconnection since enrolment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A blacklist here, and an allowlist for <c>FILE_INFO</c> - deliberately opposite</b>, because
    /// the risks are opposite: unbounded attacker-influenced gcode headers there, a small closed
    /// firmware-rendered object here that <see cref="UnknownFieldTracker"/> exists to watch - and an
    /// allowlist would silently drop the new firmware fields that tracker is meant to surface.
    /// </para>
    /// <para>
    /// <b>Masked rather than dropped</b>, so the field's presence stays visible. <b>This does not
    /// scrub rows already written</b> - a deployment that cares clears them out of band.
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
    /// which is more machinery than a list of three earns.
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
}
