using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Merges a <see cref="TelemetryDTO"/> into a <see cref="PrinterLiveState"/>, and projects the
/// merged result into a dense <see cref="TelemetrySample"/>. See phase-3 notes
/// (<c>notes/phase-3-persistence.md</c>) for why this has to be a two-step merge-then-snapshot
/// rather than writing the raw message straight to a sample row.
/// </summary>
/// <remarks>
/// Pure data transform - no I/O, no clock reads - so <see cref="TelemetryWriter"/> can own the
/// in-memory per-printer cache and call this synchronously while draining its channel, and this
/// class can be unit tested without a <c>BackgroundService</c>/channel harness.
/// </remarks>
public static class PrinterLiveStateMerger
{
    /// <summary>
    /// Overwrites only the fields <paramref name="telemetry"/> actually carries, leaving
    /// everything else at its last-known value - see <see cref="PrinterLiveState"/>'s own remarks
    /// for why a null on the DTO must never blank out a previously-reported value.
    /// </summary>
    public static void Merge(PrinterLiveState state, TelemetryDTO telemetry, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(telemetry);

        state.LastSeenAt = receivedAt;

        // Always present on the wire (TelemetryDTO.Status is required), so always overwrites -
        // unlike everything below it.
        state.Status = PrinterStatusExtensions.ParseWireState(telemetry.Status);

        MergeJob(state, telemetry);

        state.ExtruderFan = telemetry.ExtruderFan ?? state.ExtruderFan;
        state.PrintFan = telemetry.PrintFan ?? state.PrintFan;
        state.FilamentUsed = telemetry.FilamentUsed ?? state.FilamentUsed;

        state.NozzleTemperature = telemetry.NozzleTemperature ?? state.NozzleTemperature;
        state.BedTemperature = telemetry.BedTemperature ?? state.BedTemperature;
        state.TargetNozzleTemperature = telemetry.TargetNozzleTemperature ?? state.TargetNozzleTemperature;
        state.TargetBedTemperature = telemetry.TargetBedTemperature ?? state.TargetBedTemperature;
        state.Speed = telemetry.Speed ?? state.Speed;
        state.Flow = telemetry.Flow ?? state.Flow;
        state.Material = telemetry.Material ?? state.Material;
        state.XAxis = telemetry.XAxis ?? state.XAxis;
        state.YAxis = telemetry.YAxis ?? state.YAxis;
        state.ZAxis = telemetry.ZAxis ?? state.ZAxis;

        state.HeatbreakTemperature = telemetry.HeatbreakTemperature ?? state.HeatbreakTemperature;
        state.PsuTemperature = telemetry.PsuTemperature ?? state.PsuTemperature;
        state.AmbientTemperature = telemetry.AmbientTemperature ?? state.AmbientTemperature;
        state.ExtruderFilamentSensorStatus =
            telemetry.ExtruderFilamentSensorStatus ?? state.ExtruderFilamentSensorStatus;
        state.RemoteFilamentSensorStatus =
            telemetry.RemoteFilamentSensorStatus ?? state.RemoteFilamentSensorStatus;

        // Chamber/enclosure/slot are each rendered by firmware as one atomic block - present or
        // absent as a whole, never partially - so these overwrite every field in the block rather
        // than coalescing field-by-field like everything above.
        if (telemetry.Chamber is { } chamber)
        {
            state.ChamberTemperature = chamber.Temperature;
            state.ChamberTargetTemperature = chamber.TargetTemperature;
            state.ChamberFan1Rpm = chamber.Fan1Speed;
            state.ChamberFan2Rpm = chamber.Fan2Speed;
            state.ChamberFanPwmTarget = chamber.FanPwmTarget;
            state.ChamberLedIntensity = chamber.LedIntensity;
        }

        if (telemetry.Enclosure is { } enclosure)
        {
            state.EnclosureTemperature = enclosure.Temperature;
            state.EnclosureFanRpm = enclosure.FanSpeed;
            state.EnclosureTimeInUse = enclosure.TimeIsUse;
        }

        if (telemetry.Slot is { } slot)
        {
            MergeSlot(state, slot);
        }
    }

    /// <summary>
    /// <see cref="SlotsTelemetryDTO.Active"/> is always present when the block is present, but
    /// <see cref="SlotsTelemetryDTO.MmuState"/>/<see cref="SlotsTelemetryDTO.MmuCommand"/> are
    /// MMU-only - an XL sends <c>slot</c> (tool-changer, &gt;1 tool) without ever populating those
    /// two, so they still need the per-field coalesce, not a block overwrite.
    /// </summary>
    /// <summary>
    /// Merges the job block, which is the one part of a telemetry message where <b>absence means
    /// something</b> - so it is cleared rather than carried when the printer stops sending it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exception to this class's whole rule, and firmware is explicit about it.</b> Everything
    /// else here keeps its last-known value on a null, because for a temperature "unchanged" and
    /// "not sent" are the same thing. For a job they are opposites, and <c>render.cpp</c> guards the
    /// entire block with <c>if (params.has_job)</c> at the pinned ref: <c>job_id</c>,
    /// <c>time_printing</c>, <c>time_remaining</c>, <c>filament_change_in</c> and <c>progress</c>
    /// arrive together or not at all. A print that ends therefore stops sending them, and carrying
    /// them forward is how a finished printer went on reporting <b>99% for nine hours</b> (Henrik,
    /// 2026-08-09) - on the Detail page, not merely in the table, since it reads this same row.
    /// </para>
    /// <para>
    /// <b>Keyed on the whole block rather than on <c>job_id</c> alone</b>, which costs four extra null
    /// checks and buys not depending on which field firmware puts first. Any one of them present means
    /// there is a job; all absent means there is not.
    /// </para>
    /// <para>
    /// <b><see cref="PrinterLiveState.FilamentUsed"/> is deliberately not here.</b> It sits outside
    /// that guard because it is a lifetime odometer rather than a property of the print, so it is
    /// still carried - a figure that stops rising when nothing extrudes is correct, where a progress
    /// that stops falling is a lie.
    /// </para>
    /// <para>
    /// <b>This does not weaken the dense-sample guarantee</b> (<c>notes/phase-3-persistence.md</c>),
    /// which is that a reader never has to interpolate across gaps. A null here is not a gap: the
    /// printer genuinely had no job, and a row saying so is complete.
    /// </para>
    /// </remarks>
    private static void MergeJob(PrinterLiveState state, TelemetryDTO telemetry)
    {
        bool hasJob = telemetry.JobId is not null
                      || telemetry.Progress is not null
                      || telemetry.TimePrinting is not null
                      || telemetry.TimeRemaining is not null
                      || telemetry.TimeToFilamentChange is not null;

        if (!hasJob)
        {
            state.JobId = null;
            state.Progress = null;
            state.TimePrinting = null;
            state.TimeRemaining = null;
            state.TimeToFilamentChange = null;

            return;
        }

        state.JobId = telemetry.JobId ?? state.JobId;
        state.Progress = telemetry.Progress ?? state.Progress;
        state.TimePrinting = telemetry.TimePrinting ?? state.TimePrinting;
        state.TimeRemaining = telemetry.TimeRemaining ?? state.TimeRemaining;
        state.TimeToFilamentChange = telemetry.TimeToFilamentChange ?? state.TimeToFilamentChange;
    }

    private static void MergeSlot(PrinterLiveState state, SlotsTelemetryDTO slot)
    {
        state.ActiveSlot = slot.Active;
        state.MmuState = slot.MmuState ?? state.MmuState;
        state.MmuCommand = slot.MmuCommand ?? state.MmuCommand;

        if (slot.Slots is null)
        {
            return;
        }

        // Only the slot numbers present in this message are touched; a slot not mentioned keeps
        // its last-known row untouched, same rule as everything else here.
        foreach ((string key, JsonElement value) in slot.Slots)
        {
            int slotNumber = int.Parse(key, CultureInfo.InvariantCulture);
            ToolTelemetryDTO? tool = value.Deserialize<ToolTelemetryDTO>();

            if (tool is null)
            {
                continue;
            }

            PrinterLiveSlotState? existing = state.Slots.FirstOrDefault(s => s.SlotNumber == slotNumber);

            if (existing is null)
            {
                existing = new PrinterLiveSlotState { PrinterId = state.PrinterId, SlotNumber = slotNumber };
                state.Slots.Add(existing);
            }

            existing.Material = tool.Material;
            existing.Temperature = tool.Temperature;
            existing.HotendFanRpm = tool.HotendFan;
            existing.PrintFanRpm = tool.PrintFan;
        }
    }

    /// <summary>
    /// Projects an already-merged <paramref name="state"/> into a new dense
    /// <see cref="TelemetrySample"/> row - see <see cref="TelemetrySample"/>'s own remarks for why
    /// samples are built from merged state rather than the raw message.
    /// </summary>
    /// <remarks>
    /// <see cref="PrinterLiveState.ExtruderFilamentSensorStatus"/>/
    /// <see cref="PrinterLiveState.RemoteFilamentSensorStatus"/> have no counterpart on
    /// <see cref="TelemetrySample"/> and are deliberately not copied - AGENT-NOTES §5 scopes the
    /// sample table to "the numeric fields worth graphing", and a discrete sensor-status string
    /// isn't one.
    /// </remarks>
    public static TelemetrySample ToSample(PrinterLiveState state, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(state);

        TelemetrySample sample = new()
        {
            PrinterId = state.PrinterId,
            Timestamp = timestamp,
            Status = state.Status,
            JobId = state.JobId,
            Progress = state.Progress,
            TimePrinting = state.TimePrinting,
            TimeRemaining = state.TimeRemaining,
            NozzleTemperature = state.NozzleTemperature,
            BedTemperature = state.BedTemperature,
            TargetNozzleTemperature = state.TargetNozzleTemperature,
            TargetBedTemperature = state.TargetBedTemperature,
            Speed = state.Speed,
            Flow = state.Flow,
            Material = state.Material,
            XAxis = state.XAxis,
            YAxis = state.YAxis,
            ZAxis = state.ZAxis,
            ExtruderFan = state.ExtruderFan,
            PrintFan = state.PrintFan,
            FilamentUsed = state.FilamentUsed,
            TimeToFilamentChange = state.TimeToFilamentChange,
            ChamberTemperature = state.ChamberTemperature,
            ChamberTargetTemperature = state.ChamberTargetTemperature,
            ChamberFan1Rpm = state.ChamberFan1Rpm,
            ChamberFan2Rpm = state.ChamberFan2Rpm,
            ChamberFanPwmTarget = state.ChamberFanPwmTarget,
            ChamberLedIntensity = state.ChamberLedIntensity,
            EnclosureTemperature = state.EnclosureTemperature,
            EnclosureFanRpm = state.EnclosureFanRpm,
            EnclosureTimeInUse = state.EnclosureTimeInUse,
            HeatbreakTemperature = state.HeatbreakTemperature,
            PsuTemperature = state.PsuTemperature,
            AmbientTemperature = state.AmbientTemperature,
            ActiveSlot = state.ActiveSlot,
            MmuState = state.MmuState,
            MmuCommand = state.MmuCommand,
        };

        foreach (PrinterLiveSlotState slot in state.Slots)
        {
            sample.Slots.Add(new TelemetrySlotSample
            {
                SlotNumber = slot.SlotNumber,
                Material = slot.Material,
                Temperature = slot.Temperature,
                HotendFanRpm = slot.HotendFanRpm,
                PrintFanRpm = slot.PrintFanRpm,
            });
        }

        return sample;
    }
}
