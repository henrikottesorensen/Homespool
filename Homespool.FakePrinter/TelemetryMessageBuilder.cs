using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// Builds the two telemetry shapes the wire actually carries, field order taken from the committed
/// capture (<c>Homespool.Host.Test/websocket.capture</c>): <b>slim</b> - state plus the job block -
/// and <b>full</b> - temperatures, speed/flow, material and the rest. The full/reduced split is
/// explicit firmware behaviour, not sampling noise (<c>SendTelemetry::Mode</c>).
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

            WriteSlotBlock(writer, readings);

            writer.WriteString("state", device.WireState);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The <c>slot</c> object: numbered per-tool sub-objects beside the fixed <c>active</c>, plus
    /// <c>state</c>/<c>command</c> on MMU builds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shape and key order from the real capture, not from our own DTO - a fake built from our
    /// model can only ever agree with us. Keys are
    /// <b>1-based</b>, and <c>fan_hotend</c>/<c>fan_print</c> are floats on the wire despite being
    /// <c>uint16_t</c> in firmware, because they are rendered with <c>JSON_FIELD_FFIXED</c>.
    /// </para>
    /// <para>
    /// <b>Emitted only when there is more than one tool</b>, matching firmware's
    /// <c>enabled_tool_cnt() &gt; 1</c> gate: a single-tool printer sends no <c>slot</c> object at
    /// all. That is why the committed capture contains none, and why the server's slot tables stayed
    /// empty through every load run until this existed.
    /// </para>
    /// <para>
    /// Per-tool values are derived from the printer-wide readings rather than tracked independently -
    /// tool 1 reports the machine's own nozzle figure and each further tool is offset by a degree, so
    /// the rows are distinguishable rather than identical. Clearly our invention, per mitigation #3:
    /// a real multi-tool printer's idle tools sit near ambient, and modelling that honestly needs
    /// per-tool state <see cref="FakeDevice"/> does not have.
    /// </para>
    /// </remarks>
    private static void WriteSlotBlock(Utf8JsonWriter writer, TelemetryReadings readings)
    {
        if (readings.Tools <= 1)
        {
            return;
        }

        writer.WriteStartObject("slot");

        for (int tool = 1; tool <= readings.Tools; tool++)
        {
            writer.WriteStartObject(tool.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("material", readings.Material);
            writer.WriteNumber("temp", readings.NozzleTemperature + tool - 1);
            writer.WriteNumber("fan_hotend", (double)readings.FanExtruder);
            writer.WriteNumber("fan_print", (double)readings.FanPrint);
            writer.WriteEndObject();
        }

        if (readings.MmuState is { } mmuState)
        {
            writer.WriteNumber("state", mmuState);
        }

        if (readings.MmuCommand is { } mmuCommand)
        {
            writer.WriteString("command", mmuCommand);
        }

        // Clamped from zero, not from one: firmware packs "no tool picked" into this field as 0
        // (render.cpp, active_slot's NoTool arm), so 0 is a value the wire really carries and not an
        // out-of-range number to be corrected away. See TelemetryReadings.ActiveTool for which
        // machines rest there and which merely pass through it.
        writer.WriteNumber("active", Math.Clamp(readings.ActiveTool, 0, readings.Tools));
        writer.WriteEndObject();
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

        // Between time_remaining and progress, and only when a pause is scheduled - the firmware
        // gates it on time_to_pause being valid (render.cpp:164), so an ordinary print omits it
        // entirely rather than sending zero. Confirmed twice: the rig sent this shape live, and
        // Prusa's own render.cpp "Telemetry - reduced" golden string carries the same six fields.
        if (readings.TimeToFilamentChange is { } untilChange)
        {
            writer.WriteNumber("filament_change_in", untilChange);
        }

        writer.WriteNumber("progress", readings.Progress);
    }
}
