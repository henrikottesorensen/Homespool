using System.Buffers;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// Builds the two telemetry shapes the wire actually carries, field order taken from the committed
/// capture (<c>Homespool.Host.Test/websocket.capture</c>): <b>slim</b> - state plus the job block -
/// and <b>full</b> - temperatures, speed/flow, material and the rest. The full/reduced split is
/// explicit firmware behaviour, not sampling noise (<c>SendTelemetry::Mode</c>; see
/// <c>notes/phase-1-storage.md</c> §12).
/// </summary>
/// <remarks>
/// The job block (<c>job_id</c>, <c>time_printing</c>, <c>time_remaining</c>, <c>progress</c>) is
/// gated on a job existing, and <c>axis_x</c>/<c>axis_y</c> are sent only when <b>not</b> printing
/// while the fan/filament fields are sent only when printing - the two groups never co-occur
/// (render.cpp:158-232). <c>state</c> is always sent, last, matching the capture.
/// </remarks>
public static class TelemetryMessageBuilder
{
    /// <summary>
    /// The slim (Reduced) shape: the job block when a job exists, otherwise just
    /// <c>{"state":"..."}</c>. Roughly 45% of real messages look like this.
    /// </summary>
    public static byte[] BuildSlim(FakeDevice device, TelemetryReadings readings)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WriteJobBlock(writer, device, readings);
            writer.WriteString("state", device.WireState);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The full shape - everything the single-tool capture printer sends, minus its chamber.</summary>
    public static byte[] BuildFull(FakeDevice device, TelemetryReadings readings)
    {
        bool printing = device.State == DeviceState.Printing;
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WriteJobBlock(writer, device, readings);
            writer.WriteNumber("temp_nozzle", readings.NozzleTemperature);
            writer.WriteNumber("temp_bed", readings.BedTemperature);
            writer.WriteNumber("target_nozzle", readings.TargetNozzle);
            writer.WriteNumber("target_bed", readings.TargetBed);
            writer.WriteNumber("speed", readings.Speed);
            writer.WriteNumber("flow", readings.Flow);
            writer.WriteString("material", readings.Material);

            if (!printing)
            {
                // Positions only when not printing - "connect doesn't want positions during
                // printing" (render.cpp:216-220).
                writer.WriteNumber("axis_x", 0.0);
                writer.WriteNumber("axis_y", 0.0);
            }

            writer.WriteNumber("axis_z", readings.AxisZ);

            if (printing)
            {
                writer.WriteNumber("fan_extruder", readings.FanExtruder);
                writer.WriteNumber("fan_print", readings.FanPrint);
                writer.WriteNumber("filament", 2428288.0);
            }

            writer.WriteString("state", device.WireState);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJobBlock(Utf8JsonWriter writer, FakeDevice device, TelemetryReadings readings)
    {
        if (!device.JobId.HasValue)
        {
            return;
        }

        writer.WriteNumber("job_id", device.JobId.Value);
        writer.WriteNumber("time_printing", readings.TimePrinting);
        writer.WriteNumber("time_remaining", readings.TimeRemaining);
        writer.WriteNumber("progress", readings.Progress);
    }
}
