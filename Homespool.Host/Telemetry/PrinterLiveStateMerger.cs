using System;
using System.Linq;

using Homespool.Model.Entities;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Applies a <see cref="TelemetryUpdate"/> to a <see cref="PrinterLiveState"/>, and projects the
/// merged result into a dense <see cref="TelemetrySample"/>. See phase-3 notes
/// (<c>notes/phase-3-persistence.md</c>) for why this has to be a two-step merge-then-snapshot
/// rather than writing the raw message straight to a sample row.
/// </summary>
/// <remarks>
/// <para>
/// Pure data transform - no I/O, no clock reads - so <see cref="TelemetryWriter"/> can own the
/// in-memory per-printer cache and call this synchronously while draining its channel.
/// </para>
/// <para>
/// <b>Deliberately protocol-free, and one rule only</b>: a present <see cref="Field{T}"/> is
/// assigned - null included, which is how a job block clears - and an absent one keeps the
/// last-known value. Every judgement about <i>which</i> fields a message speaks for belongs to
/// the protocol edge that heard it (<c>PrusaTelemetryMapping</c> for Prusa Connect), because the
/// answer differs per protocol and even per model - <c>notes/domain-vocabulary.md</c> and the
/// Bambu X1/P1 split in <c>notes/bambu-protocol.md</c>.
/// </para>
/// </remarks>
public static class PrinterLiveStateMerger
{
    /// <summary>
    /// Applies every field the update speaks for and leaves the rest at last-known - the entire
    /// merge, with the policy already decided upstream.
    /// </summary>
    public static void Apply(PrinterLiveState state, TelemetryUpdate update, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(update);

        state.LastSeenAt = receivedAt;
        state.Status = update.Status;

        if (update.JobId.IsPresent)
        {
            state.JobId = update.JobId.Value;
        }

        if (update.Progress.IsPresent)
        {
            state.Progress = update.Progress.Value;
        }

        if (update.TimePrinting.IsPresent)
        {
            state.TimePrinting = update.TimePrinting.Value;
        }

        if (update.TimeRemaining.IsPresent)
        {
            state.TimeRemaining = update.TimeRemaining.Value;
        }

        if (update.TimeToFilamentChange.IsPresent)
        {
            state.TimeToFilamentChange = update.TimeToFilamentChange.Value;
        }

        if (update.ExtruderFan.IsPresent)
        {
            state.ExtruderFan = update.ExtruderFan.Value;
        }

        if (update.PrintFan.IsPresent)
        {
            state.PrintFan = update.PrintFan.Value;
        }

        if (update.FilamentUsed.IsPresent)
        {
            state.FilamentUsed = update.FilamentUsed.Value;
        }

        if (update.NozzleTemperature.IsPresent)
        {
            state.NozzleTemperature = update.NozzleTemperature.Value;
        }

        if (update.BedTemperature.IsPresent)
        {
            state.BedTemperature = update.BedTemperature.Value;
        }

        if (update.TargetNozzleTemperature.IsPresent)
        {
            state.TargetNozzleTemperature = update.TargetNozzleTemperature.Value;
        }

        if (update.TargetBedTemperature.IsPresent)
        {
            state.TargetBedTemperature = update.TargetBedTemperature.Value;
        }

        if (update.Speed.IsPresent)
        {
            state.Speed = update.Speed.Value;
        }

        if (update.Flow.IsPresent)
        {
            state.Flow = update.Flow.Value;
        }

        if (update.Material.IsPresent)
        {
            state.Material = update.Material.Value;
        }

        if (update.XAxis.IsPresent)
        {
            state.XAxis = update.XAxis.Value;
        }

        if (update.YAxis.IsPresent)
        {
            state.YAxis = update.YAxis.Value;
        }

        if (update.ZAxis.IsPresent)
        {
            state.ZAxis = update.ZAxis.Value;
        }

        if (update.HeatbreakTemperature.IsPresent)
        {
            state.HeatbreakTemperature = update.HeatbreakTemperature.Value;
        }

        if (update.PsuTemperature.IsPresent)
        {
            state.PsuTemperature = update.PsuTemperature.Value;
        }

        if (update.AmbientTemperature.IsPresent)
        {
            state.AmbientTemperature = update.AmbientTemperature.Value;
        }

        if (update.ExtruderFilamentSensorStatus.IsPresent)
        {
            state.ExtruderFilamentSensorStatus = update.ExtruderFilamentSensorStatus.Value;
        }

        if (update.RemoteFilamentSensorStatus.IsPresent)
        {
            state.RemoteFilamentSensorStatus = update.RemoteFilamentSensorStatus.Value;
        }

        if (update.ChamberTemperature.IsPresent)
        {
            state.ChamberTemperature = update.ChamberTemperature.Value;
        }

        if (update.ChamberTargetTemperature.IsPresent)
        {
            state.ChamberTargetTemperature = update.ChamberTargetTemperature.Value;
        }

        if (update.ChamberFan1Rpm.IsPresent)
        {
            state.ChamberFan1Rpm = update.ChamberFan1Rpm.Value;
        }

        if (update.ChamberFan2Rpm.IsPresent)
        {
            state.ChamberFan2Rpm = update.ChamberFan2Rpm.Value;
        }

        if (update.ChamberFanPwmTarget.IsPresent)
        {
            state.ChamberFanPwmTarget = update.ChamberFanPwmTarget.Value;
        }

        if (update.ChamberLedIntensity.IsPresent)
        {
            state.ChamberLedIntensity = update.ChamberLedIntensity.Value;
        }

        if (update.EnclosureTemperature.IsPresent)
        {
            state.EnclosureTemperature = update.EnclosureTemperature.Value;
        }

        if (update.EnclosureFanRpm.IsPresent)
        {
            state.EnclosureFanRpm = update.EnclosureFanRpm.Value;
        }

        if (update.EnclosureTimeInUse.IsPresent)
        {
            state.EnclosureTimeInUse = update.EnclosureTimeInUse.Value;
        }

        if (update.ActiveSlot.IsPresent)
        {
            state.ActiveSlot = update.ActiveSlot.Value;
        }

        if (update.MmuState.IsPresent)
        {
            state.MmuState = update.MmuState.Value;
        }

        if (update.MmuCommand.IsPresent)
        {
            state.MmuCommand = update.MmuCommand.Value;
        }

        foreach (SlotUpdate slot in update.Slots)
        {
            PrinterLiveSlotState? existing = state.Slots.FirstOrDefault(s => s.SlotNumber == slot.SlotNumber);

            if (existing is null)
            {
                existing = new PrinterLiveSlotState { PrinterId = state.PrinterId, SlotNumber = slot.SlotNumber };
                state.Slots.Add(existing);
            }

            existing.Material = slot.Material;
            existing.Temperature = slot.Temperature;
            existing.HotendFanRpm = slot.HotendFanRpm;
            existing.PrintFanRpm = slot.PrintFanRpm;
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
