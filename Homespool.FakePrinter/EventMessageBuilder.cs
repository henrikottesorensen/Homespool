using System;
using System.Buffers;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// Builds printer-to-server event JSON in the firmware's own field order
/// (Prusa-Firmware-Buddy <c>render.cpp:268-679</c>, <c>render_msg</c> for <c>Event</c>, at the
/// pinned ref): optional <c>job_id</c>, optional <c>reason</c>, the <c>data</c> block, then always
/// <c>state</c>, optional <c>command_id</c>, and <c>event</c> <b>last</b>. The order is not load-
/// bearing for JSON semantics, but a fake that scrambles it stops looking like the reference
/// client - and byte-shape fidelity is this library's whole reason to exist.
/// </summary>
public static class EventMessageBuilder
{
    /// <summary>
    /// A plain event - the ack shapes (<c>FINISHED</c>, <c>REJECTED</c>, <c>ACCEPTED</c>,
    /// <c>STATE_CHANGED</c>) and anything else without a <c>data</c> block.
    /// </summary>
    /// <param name="eventType">The wire event name, e.g. <c>FINISHED</c> (planner.cpp:266-300).</param>
    /// <param name="state">The wire device state, always present (render.cpp:671).</param>
    /// <param name="commandId">The command being answered, when this event is an answer.</param>
    /// <param name="reason">Rejection reason; rendered near the front like the firmware does.</param>
    /// <param name="jobId">
    /// Only passed for event types that carry extras and only while a job exists - never on
    /// <c>ACCEPTED</c>/<c>REJECTED</c> (render.cpp:271, <c>has_extra</c>).
    /// </param>
    public static byte[] Build(string eventType, string state, uint? commandId = null, string? reason = null, int? jobId = null)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            if (jobId.HasValue)
            {
                writer.WriteNumber("job_id", jobId.Value);
            }

            if (reason is not null)
            {
                writer.WriteString("reason", reason);
            }

            writer.WriteString("state", state);

            if (commandId.HasValue)
            {
                writer.WriteNumber("command_id", commandId.Value);
            }

            writer.WriteString("event", eventType);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The <c>INFO</c> event. The firmware guarantees one on every connection -
    /// <c>Planner::reset()</c> marks the info dirty and runs on init and on every reconnect
    /// (planner.cpp:347) - so <see cref="FakePrinterClient"/> sends this first, always.
    /// </summary>
    /// <remarks>
    /// The <c>data</c> block mirrors the single real INFO in the committed capture
    /// (<c>Homespool.Host.Test/websocket.capture</c>, a Core One on 6.4.0) with this identity's
    /// values substituted, minus the model-specific extras (<c>mmu</c>, <c>addon_power</c>). Note
    /// it carries the <b>full 50-character</b> fingerprint and the serial - the one place either
    /// appears on <c>/p/ws</c> (see <c>notes/cross-channel-identity-bug.md</c>).
    /// </remarks>
    public static byte[] BuildInfo(PrinterIdentity identity, string state, uint? commandId = null, int? jobId = null)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            if (jobId.HasValue)
            {
                writer.WriteNumber("job_id", jobId.Value);
            }

            writer.WriteStartObject("data");
            writer.WriteString("firmware", identity.Firmware);
            writer.WriteString("printer_type", identity.PrinterType);
            writer.WriteString("sn", identity.SerialNumber);
            writer.WriteBoolean("appendix", false);
            writer.WriteString("fingerprint", identity.Fingerprint);
            writer.WriteNumber("nozzle_diameter", 0.40);
            writer.WriteBoolean("transfer_paused", false);

            writer.WriteStartArray("storages");
            writer.WriteStartObject();
            writer.WriteString("mountpoint", "/usb");
            writer.WriteString("type", "USB");
            writer.WriteBoolean("read_only", false);
            writer.WriteNumber("free_space", 63729893376);
            writer.WriteBoolean("is_sfn", true);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("network_info");
            writer.WriteString("wifi_ssid", "FakePrinterNet");
            writer.WriteString("wifi_mac", "00:00:00:00:00:00");
            writer.WriteString("wifi_ipv4", "192.168.0.123");
            writer.WriteString("hostname", "fake-printer");
            writer.WriteEndObject();

            writer.WriteStartObject("tools");
            writer.WriteStartObject("1");
            writer.WriteNumber("nozzle_diameter", 0.40);
            writer.WriteBoolean("high_flow", false);
            writer.WriteBoolean("hardened", false);
            writer.WriteString("material", "PLA");
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteNumber("slots", 1);
            writer.WriteEndObject();

            writer.WriteString("state", state);

            if (commandId.HasValue)
            {
                writer.WriteNumber("command_id", commandId.Value);
            }

            writer.WriteString("event", "INFO");
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
