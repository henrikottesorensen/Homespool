using System;
using System.Buffers;
using System.Collections.Generic;
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
    /// <param name="machineReason">
    /// The machine-readable companion to <paramref name="reason"/> (<c>to_str(MachineReason)</c>,
    /// planner.cpp:302-317) - e.g. <c>TRANSFER_IN_PROGRESS</c>. Firmware attaches one only to the
    /// rejections that have a code; the job-control refusals carry a reason string alone.
    /// </param>
    public static byte[] Build(string eventType,
                               string state,
                               uint? commandId = null,
                               string? reason = null,
                               int? jobId = null,
                               string? machineReason = null)
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

            if (machineReason is not null)
            {
                writer.WriteString("machine_reason", machineReason);
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
    /// <c>TRANSFER_INFO</c> - what a <c>START_CONNECT_DOWNLOAD</c> is answered with
    /// (planner.cpp:801-824), <b>not</b> <c>FINISHED</c>.
    /// </summary>
    /// <param name="state">The wire device state.</param>
    /// <param name="transfer">The transfer being reported.</param>
    /// <param name="commandId">The command this answers, when it answers one.</param>
    /// <param name="timeRemaining">Firmware's own estimate; the fake has no basis for one.</param>
    /// <param name="timeTransferring">Seconds elapsed.</param>
    /// <remarks>
    /// Data-block order is render.cpp:518-533: <c>size</c>, <c>transferred</c>, <c>progress</c>,
    /// <c>time_remaining</c>, <c>time_transferring</c>, <c>path</c>, <c>start_cmd_id</c>, <c>type</c>.
    /// <c>progress</c> is a <b>percentage to one decimal</b>, not a fraction. <c>start_cmd_id</c> sits
    /// inside <c>data</c> while <c>transfer_id</c> sits at the root - easy to get backwards, and the
    /// server's DTO carries a warning about exactly that.
    /// </remarks>
    public static byte[] BuildTransferInfo(string state,
                                           FakeTransfer transfer,
                                           uint? commandId = null,
                                           int timeRemaining = 0,
                                           int timeTransferring = 0)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("data");
            writer.WriteNumber("size", transfer.TotalSize);
            writer.WriteNumber("transferred", transfer.ValidSize);
            writer.WriteNumber("progress", Math.Round(transfer.ValidSize * 100.0 / transfer.TotalSize, 1));
            writer.WriteNumber("time_remaining", timeRemaining);
            writer.WriteNumber("time_transferring", timeTransferring);
            writer.WriteString("path", transfer.Path);
            writer.WriteNumber("start_cmd_id", transfer.StartCommandId);
            writer.WriteString("type", "FROM_CONNECT");
            writer.WriteEndObject();

            writer.WriteString("state", state);

            if (commandId.HasValue)
            {
                writer.WriteNumber("command_id", commandId.Value);
            }

            writer.WriteNumber("transfer_id", transfer.TransferId);
            writer.WriteString("event", "TRANSFER_INFO");
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A transfer's terminal event - <c>TRANSFER_FINISHED</c>, <c>TRANSFER_ABORTED</c> or
    /// <c>TRANSFER_STOPPED</c>.
    /// </summary>
    /// <param name="eventType">Which ending. A chunk the printer rejects produces
    /// <c>TRANSFER_ABORTED</c>: <c>FailedRemote</c> becomes <c>State::Failed</c> with
    /// <c>Outcome::ErrorOther</c> (transfer.cpp:390), which renders as Aborted
    /// (planner.cpp:474-475).</param>
    /// <param name="state">The wire device state.</param>
    /// <param name="transferId">Rendered at the <b>root</b>, which is what lets a server match the
    /// ending to the transfer even after a <c>RangeJump</c> changed the <c>file_id</c>.</param>
    /// <param name="startCommandId">
    /// The command that started it, or null for a transfer the server did not start - in which case
    /// <b>no <c>data</c> object is emitted at all</b> (render.cpp:538-543), a shape confirmed on a
    /// real wire in <c>notes/protocol-reference.md</c>.
    /// </param>
    /// <remarks>These are unsolicited reports, so they carry no <c>command_id</c>.</remarks>
    public static byte[] BuildTransferTerminal(string eventType, string state, int transferId, uint? startCommandId)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            if (startCommandId.HasValue)
            {
                writer.WriteStartObject("data");
                writer.WriteNumber("start_cmd_id", startCommandId.Value);
                writer.WriteEndObject();
            }

            writer.WriteString("state", state);
            writer.WriteNumber("transfer_id", transferId);
            writer.WriteString("event", eventType);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// <c>FILE_INFO</c> for a single file - what a printer reports when a file appears, and what a
    /// completed transfer produces.
    /// </summary>
    /// <param name="state">The wire device state.</param>
    /// <param name="path">The file's path, which for a transfer is the one the server asked for.</param>
    /// <param name="size">Bytes on disk.</param>
    /// <param name="modified">The file's mtime as a Unix timestamp.</param>
    /// <param name="commandId">Set when this answers a <c>SEND_FILE_INFO</c>.</param>
    /// <remarks>
    /// <para>
    /// <b>Not <c>FILE_CHANGED</c>.</b> One ternary picks between them (planner.cpp:503) and a created
    /// <i>file</i> takes this arm; <c>FILE_CHANGED</c> is for deletions, folder creations and the
    /// combined case. Two notes in this repository had that backwards - see
    /// <c>notes/protocol-reference.md</c>, "<c>FILE_INFO</c> vs <c>FILE_CHANGED</c>".
    /// </para>
    /// <para>
    /// Field order is render.cpp:466-498. Two <b>documented deviations</b> from what real hardware
    /// puts on the wire, both because closing them means inventing data rather than reporting it:
    /// </para>
    /// <para>
    /// <b>1. No <c>preview</c> or gcode-metadata block.</b> Producing one means parsing a gcode for a
    /// thumbnail. Firmware genuinely omits the preview when a file has none (render.cpp:791-795), so
    /// an absent one is a real shape - but note that on hardware this is the field that makes
    /// <c>FILE_INFO</c> the largest message a printer sends: 92 831 bytes across 184 continuation
    /// frames, measured in the captures. Nothing here exercises that.
    /// </para>
    /// <para>
    /// <b>2. No 8.3 aliasing.</b> On real hardware <c>path</c> is the <b>short</b> name and
    /// <c>display_name</c> the long one - the renderer converts in place before emitting
    /// (<c>get_SFN_path</c>/<c>get_LFN</c>, render.cpp:1134-1135), whatever path the event was built
    /// from. Captured listings show 205 of 206 entries aliased. This fake has no FAT filesystem
    /// underneath and does not model the <c>~N</c> collision index, so it puts the long name in both
    /// fields - the same simplification the Buddy rig makes with its <c>strlcpy</c> stub. The two
    /// fields are still filled from their proper sources (full path, then basename), so a consumer
    /// reads the right field even though the values coincide here. <b>Do not take a green test against
    /// this fake as evidence that short names are handled.</b>
    /// </para>
    /// </remarks>
    public static byte[] BuildFileInfo(string state, string path, long size, long modified, uint? commandId = null)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("data");
            writer.WriteNumber("size", size);
            writer.WriteNumber("m_timestamp", modified);
            writer.WriteBoolean("read_only", false);
            writer.WriteString("display_name", NameOf(path));
            writer.WriteString("type", "PRINT_FILE");
            writer.WriteString("path", path);
            writer.WriteEndObject();

            writer.WriteString("state", state);

            if (commandId.HasValue)
            {
                writer.WriteNumber("command_id", commandId.Value);
            }

            writer.WriteString("event", "FILE_INFO");
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// <c>FILE_INFO</c> for a <i>directory</i> - the <c>DirRenderer</c> variant, which is how a
    /// storage listing is obtained at all. There is no "list files" command in the 26-command
    /// vocabulary; <c>SEND_FILE_INFO</c> on a directory enumerates it (render.cpp:1006-1068).
    /// </summary>
    /// <param name="state">The wire device state.</param>
    /// <param name="path">The directory asked about.</param>
    /// <param name="children">Its direct entries, already filtered to one level.</param>
    /// <param name="commandId">The <c>SEND_FILE_INFO</c> this answers.</param>
    /// <remarks>
    /// <para>
    /// Field order and shape are the renderer's: <c>children</c> then <c>file_count</c>, with each
    /// child carrying <c>name</c> (the short name on hardware) beside <c>display_name</c> (the long
    /// one). <c>file_count</c> is rendered beside the array rather than derived from it, exactly as
    /// firmware does.
    /// </para>
    /// <para>
    /// The same <b>no 8.3 aliasing</b> deviation as <see cref="BuildFileInfo"/> applies, and here it
    /// is at its most visible: on hardware <c>name</c> and <c>display_name</c> differ for nearly
    /// every entry, and here they are equal. The two are still written from their proper sources, so
    /// a consumer that reads the wrong one is still wrong - it just cannot be caught here. See
    /// <c>FakeStorage</c>'s remarks for where the aliased case is covered instead.
    /// </para>
    /// <para>
    /// Three firmware behaviours are deliberately not modelled, because a fake drive cannot produce
    /// them honestly: dot-files being skipped, an in-progress transfer appearing as a read-only
    /// regular file, and an incomplete <c>.bbf</c> being hidden entirely (render.cpp:1017-1036).
    /// </para>
    /// </remarks>
    public static byte[] BuildFolderInfo(string state,
                                         string path,
                                         IReadOnlyList<FakeStorageEntry> children,
                                         uint? commandId = null)
    {
        ArgumentNullException.ThrowIfNull(children);

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("data");
            writer.WriteStartArray("children");

            foreach (FakeStorageEntry child in children)
            {
                writer.WriteStartObject();
                writer.WriteString("name", NameOf(child.Path));
                writer.WriteString("display_name", NameOf(child.Path));

                if (!child.IsFolder)
                {
                    writer.WriteNumber("size", child.Size);
                    writer.WriteNumber("m_timestamp", child.Modified);
                }

                writer.WriteBoolean("read_only", false);
                writer.WriteString("type", child.IsFolder ? "FOLDER" : "PRINT_FILE");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteNumber("file_count", children.Count);
            writer.WriteBoolean("read_only", false);
            writer.WriteString("display_name", NameOf(path));
            writer.WriteString("type", "FOLDER");
            writer.WriteString("path", path);
            writer.WriteEndObject();

            writer.WriteString("state", state);

            if (commandId.HasValue)
            {
                writer.WriteNumber("command_id", commandId.Value);
            }

            writer.WriteString("event", "FILE_INFO");
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string NameOf(string path)
    {
        int separator = path.LastIndexOf('/');

        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>
    /// What <c>INFO</c> reports as free space on <c>/usb</c> when nobody says otherwise - the figure a
    /// real Core One reported, 63.7 GB.
    /// </summary>
    /// <remarks>
    /// A default rather than a constant, because the queue's loop is meant to consult this before
    /// pushing a file ahead of a print, and a value nothing can change makes that check untestable.
    /// See <see cref="FakeDevice.FreeSpace"/>.
    /// </remarks>
    public const long DefaultFreeSpace = 63729893376;

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
    public static byte[] BuildInfo(PrinterIdentity identity,
                                   string state,
                                   uint? commandId = null,
                                   int? jobId = null,
                                   long freeSpace = DefaultFreeSpace)
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
            writer.WriteNumber("free_space", freeSpace);
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
