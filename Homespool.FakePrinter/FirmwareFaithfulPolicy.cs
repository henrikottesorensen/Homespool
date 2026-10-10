using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Homespool.FakePrinter;

/// <summary>
/// Answers commands the way the firmware's planner does
/// (Prusa-Firmware-Buddy <c>planner.cpp:660-800, 1075-1112</c> at the pinned ref): the quick
/// job-control commands answer <c>FINISHED</c> or <c>REJECTED</c> directly per the device state,
/// gcode runs as a background command (<c>ACCEPTED</c> now, <c>FINISHED</c> later, other commands
/// rejected as busy in between), a repeated command id is refused, and <c>SEND_INFO</c> yields an
/// <c>INFO</c> event carrying the command id.
/// </summary>
public sealed partial class FirmwareFaithfulPolicy : CommandAnswerPolicy
{
    private readonly PrinterIdentity _identity;
    private readonly TimeProvider _time;
    private uint? _lastCommandId;
    private uint? _backgroundCommandId;
    private long _backgroundDoneAt;

    /// <summary>Creates the policy; the identity feeds <c>SEND_INFO</c>'s INFO event.</summary>
    public FirmwareFaithfulPolicy(PrinterIdentity identity, TimeProvider timeProvider)
    {
        _identity = identity;
        _time = timeProvider;
    }

    /// <summary>
    /// How long a gcode background command stays "processing" - the window in which other commands
    /// are rejected with "Processing other command" and a resend of the same id is re-Accepted.
    /// </summary>
    public TimeSpan GcodeExecutionTime { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Forces the order a transfer fetches its ranges in. Null - the default - picks it the way
    /// firmware does, from the file's name and size, which means a plain gcode over half a megabyte
    /// performs a <c>RangeJump</c> without being asked to. Set this only to provoke one from a small
    /// file.
    /// </summary>
    public FakeDownloadOrder? DownloadOrder { get; init; }

    /// <summary>
    /// Supplies each negotiation's <c>file_id</c>. Null uses a random one, as <c>rand_u()</c> does;
    /// a test that needs to predict or collide with an id passes its own.
    /// </summary>
    public Func<uint>? FileIdSource { get; init; }

    /// <summary>
    /// Whether a transfer reports its partial file - a <c>FILE_INFO</c>, <c>read_only</c> and at full
    /// size - after its first chunk, as well as the finished file at the end. True by default,
    /// because firmware does: every transfer Homespool started on the appliance reported it before
    /// it ended (88 of 88), a median 4 s after the command was answered - the smallest, 64 KB, too.
    /// </summary>
    /// <remarks>
    /// Firmware's report comes from <c>notify_created</c> at a backup checkpoint once the partial is
    /// printable (transfer.cpp:262), so its exact timing depends on backup intervals this fake does
    /// not model. After the first chunk is the nearest honest place, and a transfer that completes on
    /// that chunk still reports the partial first. False reproduces a transfer that fails before
    /// reporting anything.
    /// </remarks>
    public bool ReportsTransferStart { get; init; } = true;

    /// <summary>
    /// Arms the <c>START_PRINT</c> false negative: the next <c>START_PRINT</c> that would have
    /// succeeded is executed - the print really begins - but answered
    /// <c>REJECTED "No job in progress"</c>, carrying the command id and the state the machine was
    /// in before it accepted. One-shot; a refused command (bad path, wrong state) does not consume
    /// it.
    /// </summary>
    /// <remarks>
    /// Hardware does this whenever the ack loses a race: the <c>JOB_INFO</c> answering a
    /// <c>START_PRINT</c> is rendered against the machine's momentary state
    /// (render.cpp:289-297), and between accepting the print and reporting <c>PRINTING</c> the
    /// machine passes through <c>PrintInit</c>, which reports <c>READY</c> with no job
    /// (printer_state.cpp:335-344) - so a successful start is answered as a rejection. A server
    /// treating that reason as terminal fails a print that is running, which is the phantom this
    /// exists to reproduce on demand.
    /// </remarks>
    public bool NextStartPrintAnswersNoJobInProgress { get; set; }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        if (frame.Kind is ServerCommandKind.TransferChunk)
        {
            // Answered *above* the busy check below, deliberately: chunks bypass the one-in-flight
            // command guard in the firmware too (connect.cpp:468), so a transfer and a command can
            // interleave freely. Reordering these two blocks would quietly change that.
            return AnswerChunk(frame, device);
        }

        if (frame.Kind is ServerCommandKind.Debug)
        {
            // 'D' is logged and thrown away (connect.cpp:411-419 vicinity of receive_command's
            // switch).
            return [];
        }

        // Busy with a background command? (connect.cpp:469-477 + planner.cpp:1094-1101: same id is
        // re-Accepted, anything else is rejected.)
        if (_backgroundCommandId.HasValue && _time.GetTimestamp() < _backgroundDoneAt)
        {
            if (frame.CommandId == _backgroundCommandId.Value)
            {
                return [Reply(EventMessageBuilder.Build("ACCEPTED", device.WireState, frame.CommandId))];
            }

            return [Reject(frame.CommandId, device, "Processing other command")];
        }

        _backgroundCommandId = null;

        // planner.cpp:1103-1110 - the same command id is never executed twice.
        if (_lastCommandId == frame.CommandId)
        {
            return [Reject(frame.CommandId, device, "Won't execute the same command multiple times")];
        }

        _lastCommandId = frame.CommandId;

        if (frame.Kind is ServerCommandKind.Gcode or ServerCommandKind.ForcedGcode)
        {
            // planner.cpp:683-689 - gcode becomes a background command: Accepted immediately,
            // Finished when it completes. The busy window is time-based here.
            _backgroundCommandId = frame.CommandId;
            _backgroundDoneAt = _time.GetTimestamp() +
                                (long)(GcodeExecutionTime.TotalSeconds * _time.TimestampFrequency);

            PlannedReply accepted = Reply(EventMessageBuilder.Build("ACCEPTED", device.WireState, frame.CommandId));

            if (UnloadedTool(frame) is int tool && device.MaterialOf(tool) is not null)
            {
                DeviceState before = device.BeginUnload();

                return
                [
                    accepted,
                    new PlannedReply(null, GcodeExecutionTime)
                    {
                        Complete = () =>
                        {
                            device.FinishUnload(tool, before);

                            return EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId);
                        },
                    },
                ];
            }

            return
            [
                accepted,
                new PlannedReply(
                    EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId),
                    GcodeExecutionTime),
            ];
        }

        return AnswerJson(frame, device);
    }

    /// <summary>
    /// The tool an <c>M702</c> in this gcode unloads, 1-based as the wire numbers tools; null for
    /// gcode that unloads nothing the fake models.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one gcode the fake acts on</b>, because it is the one whose effect the server reads
    /// back: the tool's material goes to <c>---</c> in telemetry. Everything else is still
    /// accepted and finished with no effect.
    /// </para>
    /// <para>
    /// <b><c>T</c> is 0-based in gcode</b> - <c>T0</c> is the wire's tool 1. An <c>M702</c> without
    /// one acts on the active tool in firmware; the fake does not track which tool is active, and the
    /// server always names one, so that form is left without an effect rather than guessed at. So is
    /// an unload of a tool already empty, which firmware answers with a dialog on the panel.
    /// </para>
    /// </remarks>
    private static int? UnloadedTool(ServerCommandFrame frame)
    {
        string gcode = Encoding.ASCII.GetString(frame.Payload.Span);

        foreach (string line in gcode.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            Match unload = UnloadLine().Match(line);

            if (unload.Success && int.TryParse(unload.Groups["tool"].ValueSpan, CultureInfo.InvariantCulture, out int index))
            {
                return index + 1;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^M702\b.*\bT(?<tool>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnloadLine();

    private static PlannedReply Reply(byte[] payload)
    {
        return new PlannedReply(payload);
    }

    private static PlannedReply RejectWithCode(uint commandId, FakeDevice device, string reason, string machineReason)
    {
        return new PlannedReply(EventMessageBuilder.Build("REJECTED", device.WireState, commandId, reason,
                                                          machineReason: machineReason));
    }

    /// <summary>
    /// <c>filename_is_transferrable</c> (filename_type.cpp:42-45): printable formats plus firmware
    /// images.
    /// </summary>
    private static bool IsTransferrable(string path)
    {
        return FakeTransfer.IsPlainGcode(path) ||
               path.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".bgc", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".bbf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the transfer's next range request, if it wants one. Silence here is correct rather than
    /// lazy: a request is only re-armed once the previous segment is fully delivered.
    /// </summary>
    private static void AppendRequest(List<PlannedReply> replies, FakeTransfer transfer)
    {
        if (transfer.NextRequest() is InlineRequest request)
        {
            replies.Add(Reply(TransferRequestBuilder.Build(request)));
        }
    }

    private static PlannedReply Reject(uint commandId, FakeDevice device, string reason)
    {
        return new PlannedReply(EventMessageBuilder.Build("REJECTED", device.WireState, commandId, reason));
    }

    /// <summary>
    /// The JC macro (planner.cpp:691-702): <c>FINISHED</c> on success, <c>REJECTED</c> with the fixed
    /// reason otherwise, and <c>FINISHED</c> carries <c>job_id</c> while a job still exists
    /// (render.cpp:271, <c>has_extra</c>).
    /// </summary>
    /// <param name="commandId">The command being answered.</param>
    /// <param name="device">The device, for the job id and the rejection's state.</param>
    /// <param name="succeeded">Whether the transition was legal from the current state.</param>
    /// <param name="rejectReason">The <c>JC</c> macro's fixed refusal text for this command.</param>
    /// <param name="stateBefore">
    /// The wire state as it was <b>before</b> the transition - which is what a real printer reports,
    /// and the opposite of what this method used to do.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Corrected 2026-07-27 against a live capture</b> (`private-captures/cubetest2.jsonl`), which
    /// contradicted the previous reading outright:
    /// </para>
    /// <code>
    /// PAUSE_PRINT   -> FINISHED +0.08s state=PRINTING   (not PAUSED)
    /// RESUME_PRINT  -> FINISHED +0.04s state=PAUSED     (not PRINTING)
    /// </code>
    /// <para>
    /// The event's <c>state</c> is not the command's outcome - it is
    /// <c>params.state.device_state</c> sampled when the event is <i>rendered</i>. Job control is
    /// asynchronous: the planner hands the request to Marlin and the ack goes out long before the
    /// machine has actually moved, so the old state is what is on the wire. <c>Finished</c> means
    /// dispatched, not done - the same capture
    /// shows a <c>STOP_PRINT</c> acked <c>FINISHED</c> in 130 ms that did nothing at all, because the
    /// machine was still mid-resume.
    /// </para>
    /// <para>
    /// <b>Deliberately not applied to the other commands.</b> The same capture shows
    /// <c>SET_PRINTER_READY</c> answering <c>STATE_CHANGED</c> with <c>state=READY</c> - the
    /// <i>new</i> state - because readiness is a local flag rather than a Marlin round trip. So this
    /// is a property of asynchronous job control, not a blanket rule about acks, and the fix is
    /// scoped to the three <c>JC</c> commands that have evidence. <c>CANCEL_PRINTER_READY</c> and
    /// <c>SET_IDLE</c> are untested either way and left reporting the new state, on the same
    /// local-flag reasoning.
    /// </para>
    /// </remarks>
    private static PlannedReply JobControl(uint commandId,
                                           FakeDevice device,
                                           bool succeeded,
                                           string rejectReason,
                                           string stateBefore)
    {
        if (!succeeded)
        {
            return Reject(commandId, device, rejectReason);
        }

        return Reply(EventMessageBuilder.Build("FINISHED", stateBefore, commandId, jobId: device.JobId));
    }

    private IReadOnlyList<PlannedReply> AnswerJson(ServerCommandFrame frame, FakeDevice device)
    {
        string? name = frame.TryGetJsonCommandName();

        if (name is null)
        {
            // command.cpp:429-431 for garbage, and UnknownCommand -> "Unknown command"
            // (planner.cpp:667-669) when the JSON parses but names nothing we know.
            string reason = frame.PayloadIsValidJson() ? "Unknown command" : "Error parsing JSON";

            return [Reject(frame.CommandId, device, reason)];
        }

        // Sampled before anything is dispatched, because the job-control acks report the state the
        // machine was still in when the event was rendered - see JobControl's remarks.
        string stateBefore = device.WireState;

        switch (name)
        {
            case "PAUSE_PRINT":
                return [JobControl(frame.CommandId, device, device.TryPause(), "No print to pause", stateBefore)];

            case "RESUME_PRINT":
                return [JobControl(frame.CommandId, device, device.TryResume(), "No paused print to resume", stateBefore)];

            case "STOP_PRINT":
                return [JobControl(frame.CommandId, device, device.TryStop(), "No print to stop", stateBefore)];

            case "SET_PRINTER_READY":
                // planner.cpp:772-776 - STATE_CHANGED on success, not FINISHED.
                return
                [
                    device.TrySetReady() ?
                        Reply(EventMessageBuilder.Build("STATE_CHANGED", device.WireState, frame.CommandId)) :
                        Reject(frame.CommandId, device, "Can't set ready now"),
                ];

            case "CANCEL_PRINTER_READY":
                // planner.cpp:778-784 - un-readying cannot fail.
                device.CancelReady();

                return [Reply(EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId))];

            case "SET_IDLE":
                // command.cpp:166 names it SET_IDLE, not SET_PRINTER_IDLE like its neighbours;
                // planner.cpp:786-790 + marlin_printer.cpp:579-586.
                return
                [
                    device.TrySetIdle() ?
                        Reply(EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId)) :
                        Reject(frame.CommandId, device, "Can't set idle now"),
                ];

            case "SEND_INFO":
                // planner.cpp:735-740 - the INFO event, carrying the command id.
                return
                [
                    Reply(EventMessageBuilder.BuildInfo(_identity, device.WireState, frame.CommandId, device.JobId,
                                                        device.FreeSpace))
                ];

            case "SEND_STATE_INFO":
                // planner.cpp:967-969 - a STATE_CHANGED naming the state, carrying the command id.
                return [Reply(EventMessageBuilder.Build("STATE_CHANGED", device.WireState, frame.CommandId, jobId: device.JobId))];

            case "START_PRINT":
                return StartPrint(frame, device);

            case "SEND_JOB_INFO":
                return SendJobInfo(frame, device);

            case "SEND_FILE_INFO":
                // planner.cpp:751-759 - the path is checked before anything is rendered, and a path
                // outside /usb is refused rather than answered.
                return SendFileInfo(frame, device);

            case "DELETE_FILE":
                return DeleteFile(frame, device);

            case "CANCEL_OBJECT":
                return CancelObject(frame, device, cancelled: true);

            case "UNCANCEL_OBJECT":
                return CancelObject(frame, device, cancelled: false);

            case "SET_VALUE":
                return SetValue(frame, device);

            case "START_CONNECT_DOWNLOAD":
            case "START_INLINE_DOWNLOAD":
                // Both spellings, one handler, because Connect sends whichever and the printer
                // decides the mechanism - which is always inline (command.cpp:186-196).
                return StartDownload(frame, device);

            default:
                return [Reject(frame.CommandId, device, "Unknown command")];
        }
    }

    /// <summary>
    /// Answers a <c>START_PRINT</c>: the path is checked, then the machine, and success is reported as
    /// <c>JOB_INFO</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the planner's</b> (planner.cpp:704-728): <c>path_allowed</c> first, then
    /// <c>is_valid_file_or_transfer</c>, and only then <c>MarlinPrinter::start_print</c>. It matters,
    /// because a bad path is refused whatever the machine is doing - the same shape as
    /// <see cref="StartDownload"/>, where the path is checked before the transfer slot is taken.
    /// </para>
    /// <para>
    /// <b>Success answers <c>JOB_INFO</c>, not <c>FINISHED</c></b> (planner.cpp:728), which makes this
    /// the one command here whose success is neither of the two usual acks. Worth knowing on the
    /// server side, where "did it work?" naturally tests for <c>FINISHED</c> and would read a print
    /// that started as an answer it did not recognise.
    /// </para>
    /// <para>
    /// <b>Five reasons are reachable</b> - <c>Forbidden path</c>, <c>File not found</c>,
    /// <c>Can't print now</c>, <c>Tools mapping not enabled</c>, and
    /// <c>No job in progress</c>, which is not a refusal at all: it is the ack of a print that
    /// <i>took</i>, rendered inside the start window - see
    /// <see cref="NextStartPrintAnswersNoJobInProgress"/>, which reproduces it on demand.
    /// <c>File is busy</c> and <c>File is being transferred</c> belong to <c>delete_file</c> and
    /// are deliberately not sent here; a server waiting on either as a busy signal would wait for
    /// something firmware cannot produce.
    /// </para>
    /// <para>
    /// A file still arriving is <b>not</b> refused: <c>is_valid_file_or_transfer</c> accepts a partial
    /// transfer, so this accepts the path of the transfer in progress as well as a file in storage,
    /// and lets the state gate decide. That is why the fake starts a print on a file whose transfer is
    /// still running, which is what hardware does.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PlannedReply> StartPrint(ServerCommandFrame frame, FakeDevice device)
    {
        string? path = PathArgument.TryParse(frame.Payload);

        if (path is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!path.StartsWith(FakeStorage.Root + "/", StringComparison.Ordinal) ||
            path.Contains("/../", StringComparison.Ordinal))
        {
            // Stricter than SEND_FILE_INFO's check by exactly one case: /usb itself is a directory,
            // and printing a directory is not a path this command has any meaning for.
            return [Reject(frame.CommandId, device, "Forbidden path")];
        }

        FakeStorageEntry? entry = device.Storage.Find(path);
        bool arriving = string.Equals(device.Transfer?.Path, path, StringComparison.Ordinal);

        if ((entry is null && !arriving) || entry?.IsFolder == true)
        {
            return [Reject(frame.CommandId, device, "File not found")];
        }

        // Sampled before the transition for the false-negative arm below: firmware renders that
        // rejection while still in a state that reports READY/IDLE, not the settled PRINTING.
        string stateBefore = device.WireState;

        if (device.TryStartPrint(path) is not int jobId)
        {
            return [Reject(frame.CommandId, device, "Can't print now")];
        }

        if (NextStartPrintAnswersNoJobInProgress)
        {
            NextStartPrintAnswersNoJobInProgress = false;

            return
            [
                Reply(EventMessageBuilder.Build("REJECTED", stateBefore, frame.CommandId, "No job in progress")),
            ];
        }

        return [Reply(EventMessageBuilder.Build("JOB_INFO", device.WireState, frame.CommandId, jobId: jobId))];
    }

    /// <summary>
    /// Answers a <c>SEND_JOB_INFO</c>: the running job describes itself, and everything else is one
    /// of three ways of saying no.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four answers, and getting the distinctions right is the point of implementing this at
    /// all.</b> They are firmware's own, from its render fixtures: the current job renders a
    /// <c>JOB_INFO</c> naming the file; a job the printer merely remembers renders a <c>JOB_INFO</c>
    /// with a <c>FIN_OK</c> state and <b>no name</b>; an id it does not recognise is
    /// <c>Rejected "Job ID doesn't match"</c>; and no job at all is
    /// <c>Rejected "No job in progress"</c>.
    /// </para>
    /// <para>
    /// <b>A fake that collapsed those into "here is the job" or "no" would be worse than not having
    /// one.</b> The server asks this question to decide whether a print it commanded and never heard
    /// back about is its own, and the whole difficulty is that two of these four answers settle
    /// nothing. A double that always answered definitely would make an unresolvable case untestable
    /// and let a loop that guesses pass.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PlannedReply> SendJobInfo(ServerCommandFrame frame, FakeDevice device)
    {
        if (JobIdArgument.TryParse(frame.Payload) is not int jobId)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (device.JobId is not int current)
        {
            return [Reject(frame.CommandId, device, "No job in progress")];
        }

        if (current != jobId)
        {
            return [Reject(frame.CommandId, device, "Job ID doesn't match")];
        }

        // A job with no path is one the machine is only remembering - the finished screen. It
        // answers, and says nothing that identifies the file.
        string? path = device.JobPath;

        return path is not null ?
            [Reply(EventMessageBuilder.BuildJobInfo(device.WireState, current, path, "PRINTING", frame.CommandId))] :
            [Reply(EventMessageBuilder.BuildJobInfo(device.WireState, current, null, "FIN_OK", frame.CommandId))];
    }

    /// <summary>
    /// Answers a <c>CANCEL_OBJECT</c> or <c>UNCANCEL_OBJECT</c> with the whole cancelled set, under the
    /// command's own id.
    /// </summary>
    /// <remarks>
    /// <b>Never <c>FINISHED</c></b>: <c>handle_cancel_object_command</c> confirms by rendering
    /// <c>CANCELABLE_CHANGED</c>, even when the flag was already where it was asked to be
    /// (planner.cpp:826-847), and the handler is synchronous, so the answer carries the new state.
    /// The one refusal is a build without the feature.
    /// </remarks>
    private IReadOnlyList<PlannedReply> CancelObject(ServerCommandFrame frame, FakeDevice device, bool cancelled)
    {
        if (ObjectIdArgument.TryParse(frame.Payload) is not int id)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!device.CancelObjectSupported)
        {
            return [Reject(frame.CommandId, device, "Not supported on this printer type")];
        }

        device.SetObjectCancelled(id, cancelled);

        return
        [
            Reply(EventMessageBuilder.BuildCancelableChanged(device.WireState, device.ObjectCount,
                                                             device.CancelledObjects, frame.CommandId,
                                                             device.JobId)),
        ];
    }

    /// <summary>
    /// Answers a <c>SET_VALUE</c> - of its dozen settings, only the LED strips' brightness, the one this
    /// application sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three answers, all firmware's own.</b> A kwarg the build does not know leaves the command with
    /// no setting at all, which is <i>"Missing or broken parameters"</i> (<c>command.cpp:425</c>); a
    /// value that will not parse as an <c>int8_t</c> is <i>"Invalid int8_t value"</i>, the
    /// <c>SET_VALUE_ARG</c> macro's <c>#type</c> spelled out; anything else is stored and answered
    /// <c>FINISHED</c>, synchronously and in any state (<c>planner.cpp:979-1062</c>).
    /// </para>
    /// <para>
    /// <b>No range check, because firmware has none</b> - see <see cref="FakeDevice.SetLedIntensity"/>.
    /// A server that sends 101 here gets <c>FINISHED</c> and a light that is nearly off, as it would
    /// from a printer.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<PlannedReply> SetValue(ServerCommandFrame frame, FakeDevice device)
    {
        JsonElement? value = LedIntensityArgument.TryFind(frame.Payload);

        // A string is not the primitive the parser matches the kwarg against, so it is as unknown as
        // a kwarg the build lacks.
        if (value is not { ValueKind: JsonValueKind.Number } number || !device.SideLedsSupported)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!number.TryGetSByte(out sbyte percent))
        {
            return [Reject(frame.CommandId, device, "Invalid int8_t value")];
        }

        device.SetLedIntensity(percent);

        return [Reply(EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId))];
    }

    /// <summary>
    /// Answers a <c>SEND_FILE_INFO</c>: a directory enumerates, a file describes itself, and a path
    /// outside <c>/usb</c> is refused before anything is rendered.
    /// </summary>
    /// <remarks>
    /// The refusal wording is firmware's own - <c>path_allowed</c> fails and the planner builds
    /// <c>Rejected{"Forbidden path"}</c> (planner.cpp:751-759). A path that is simply absent gets the
    /// same treatment here: firmware would fail inside the renderer instead, but a refusal is the
    /// honest answer a fake can give without inventing a second failure shape.
    /// </remarks>
    private IReadOnlyList<PlannedReply> SendFileInfo(ServerCommandFrame frame, FakeDevice device)
    {
        string? path = PathArgument.TryParse(frame.Payload);

        if (path is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        bool onUsb = path.StartsWith(FakeStorage.Root + "/", StringComparison.Ordinal) ||
                     string.Equals(path, FakeStorage.Root, StringComparison.Ordinal);

        if (!onUsb || path.Contains("/../", StringComparison.Ordinal))
        {
            return [Reject(frame.CommandId, device, "Forbidden path")];
        }

        FakeStorageEntry? entry = device.Storage.Find(path);

        if (entry is null)
        {
            return [Reject(frame.CommandId, device, "File not found")];
        }

        return entry.IsFolder ?
            [
                Reply(EventMessageBuilder.BuildFolderInfo(device.WireState, path,
                                                          device.Storage.Children(path), frame.CommandId))
            ] :
            [
                Reply(EventMessageBuilder.BuildFileInfo(device.WireState, path, entry.Size, entry.Modified,
                                                        frame.CommandId, entry.ObjectsInfo, entry.BedShape))
            ];
    }

    /// <summary>
    /// Answers a <c>DELETE_FILE</c>: the path is checked, then whether the file is in use, and success
    /// is a <c>FILE_CHANGED</c> under the command's id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the planner's</b> (planner.cpp:882-900): <c>path_allowed</c>, then
    /// <c>is_valid_file_or_transfer</c> - which accepts the path of a transfer still running - and
    /// only then <c>delete_file</c>, which refuses a file being printed as <c>File is busy</c> and one
    /// still arriving as <c>File is being transferred</c> (marlin_printer.cpp:549-560).
    /// </para>
    /// <para>
    /// <b>Never <c>FINISHED</c></b>: firmware leaves the answer to the change it reports, so a server
    /// waiting for a verdict waits out its timeout on a delete that worked.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PlannedReply> DeleteFile(ServerCommandFrame frame, FakeDevice device)
    {
        string? path = PathArgument.TryParse(frame.Payload);

        if (path is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!path.StartsWith(FakeStorage.Root + "/", StringComparison.Ordinal) ||
            path.Contains("/../", StringComparison.Ordinal))
        {
            return [Reject(frame.CommandId, device, "Forbidden path")];
        }

        FakeStorageEntry? entry = device.Storage.Find(path);
        bool arriving = string.Equals(device.Transfer?.Path, path, StringComparison.Ordinal);

        if ((entry is null && !arriving) || entry?.IsFolder == true)
        {
            return [Reject(frame.CommandId, device, "File not found")];
        }

        if (string.Equals(device.JobPath, path, StringComparison.Ordinal))
        {
            return [Reject(frame.CommandId, device, "File is busy")];
        }

        if (arriving)
        {
            return [Reject(frame.CommandId, device, "File is being transferred")];
        }

        device.Storage.Remove(path);

        return [Reply(EventMessageBuilder.BuildFileDeleted(device.WireState, path, device.FreeSpace, frame.CommandId))];
    }

    /// <summary>
    /// Accepts a download and opens the negotiation: <c>TRANSFER_INFO</c> - <b>not</b>
    /// <c>FINISHED</c> - followed immediately by the first range request.
    /// </summary>
    /// <remarks>
    /// The rejection arms are <c>handle_transfer_result</c>'s (planner.cpp:801-824), each with its
    /// machine-readable code. The ordering matters: firmware checks the path before it tries to take
    /// the transfer slot (<c>init_transfer</c>, planner.cpp:209-221 runs before
    /// <c>Transfer::begin</c>), so a bad path is refused even while another transfer is running.
    /// </remarks>
    private IReadOnlyList<PlannedReply> StartDownload(ServerCommandFrame frame, FakeDevice device)
    {
        StartDownloadArguments? arguments = StartDownloadArguments.TryParse(frame.Payload);

        if (arguments is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!arguments.Path.StartsWith("/usb/", StringComparison.Ordinal) ||
            arguments.Path.Contains("/../", StringComparison.Ordinal))
        {
            // path_allowed, planner.cpp:135-141.
            return [RejectWithCode(frame.CommandId, device, "Not allowed outside /usb", "STORAGE_FAILURE")];
        }

        if (!IsTransferrable(arguments.Path))
        {
            // filename_is_transferrable - printable formats plus firmware images (filename_type.cpp).
            return [RejectWithCode(frame.CommandId, device, "Unsupported file type", "STORAGE_FAILURE")];
        }

        // Transfer::begin takes the slot and then refuses a destination that exists
        // (transfer.cpp:96-107, answered at planner.cpp:815-816): a printer never overwrites a file by
        // download. So a busy slot is the answer when both apply, and only a free one gets this.
        if (device.Transfer is null && device.Storage.Find(arguments.Path) is not null)
        {
            return [RejectWithCode(frame.CommandId, device, "File already exists", "FILE_EXISTS")];
        }

        FakeTransfer? transfer = device.TryBeginTransfer(arguments.Hash, arguments.TeamId, arguments.Path,
                                                         arguments.OriginalSize, frame.CommandId, DownloadOrder, FileIdSource);

        if (transfer is null)
        {
            return [RejectWithCode(frame.CommandId, device, "Another transfer in progress", "TRANSFER_IN_PROGRESS")];
        }

        // The command id is both the answer's command_id and the transfer's start_cmd_id
        // (planner.cpp:809-812), which is what later terminal events point back at.
        List<PlannedReply> replies =
        [
            Reply(EventMessageBuilder.BuildTransferInfo(device.WireState, transfer, frame.CommandId)),
        ];

        AppendRequest(replies, transfer);

        return replies;
    }

    /// <summary>
    /// Takes one <c>'T'</c> chunk and either asks for the next range, ends the transfer, or kills it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chunk arriving with no transfer running is <b>blackholed, not answered</b>
    /// (planner.cpp:1191-1200) - which is what makes the server's "reply after the transfer ended"
    /// race harmless in both directions.
    /// </para>
    /// <para>
    /// On success the printer emits <c>TRANSFER_FINISHED</c> and then a <c>FILE_INFO</c> for the file
    /// that now exists. The first accepted chunk also reports the partial, unless
    /// <see cref="ReportsTransferStart"/> is off - see there for how that timing approximates
    /// firmware's.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PlannedReply> AnswerChunk(ServerCommandFrame frame, FakeDevice device)
    {
        FakeTransfer? transfer = device.Transfer;

        if (transfer is null)
        {
            return [];
        }

        ChunkOutcome outcome = transfer.AcceptChunk(frame.CommandId, frame.Payload.Span);

        switch (outcome)
        {
            case ChunkOutcome.Accepted:
                List<PlannedReply> replies = [];

                if (ReportsTransferStart && !transfer.StartReported)
                {
                    transfer.StartReported = true;
                    replies.Add(Reply(EventMessageBuilder.BuildFileInfo(device.WireState, transfer.Path, transfer.TotalSize,
                                                                        _time.GetUtcNow().ToUnixTimeSeconds(),
                                                                        readOnly: true)));
                }

                AppendRequest(replies, transfer);

                return replies;

            case ChunkOutcome.Completed:
                device.EndTransfer();

                // The file is now on the drive, so a later SEND_FILE_INFO finds it. Without this the
                // fake would report a transfer finishing and then deny the file exists, which is the
                // kind of incoherence that makes an end-to-end test prove nothing.
                long completedAt = _time.GetUtcNow().ToUnixTimeSeconds();

                device.Storage.AddFile(transfer.Path, transfer.TotalSize, completedAt);

                List<PlannedReply> finished = [];

                if (ReportsTransferStart && !transfer.StartReported)
                {
                    transfer.StartReported = true;
                    finished.Add(Reply(EventMessageBuilder.BuildFileInfo(device.WireState, transfer.Path, transfer.TotalSize,
                                                                         completedAt, readOnly: true)));
                }

                finished.Add(Reply(EventMessageBuilder.BuildTransferTerminal("TRANSFER_FINISHED", device.WireState,
                                                                             transfer.TransferId, transfer.StartCommandId)));
                finished.Add(Reply(EventMessageBuilder.BuildFileInfo(device.WireState, transfer.Path, transfer.TotalSize,
                                                                     completedAt)));

                return finished;

            default:
                // FailedRemote -> State::Failed -> Outcome::ErrorOther (transfer.cpp:390), which the
                // planner renders as TRANSFER_ABORTED (planner.cpp:474-475). No retry exists for this
                // class of failure, which is the property most worth reproducing.
                device.EndTransfer();

                return
                [
                    Reply(EventMessageBuilder.BuildTransferTerminal("TRANSFER_ABORTED", device.WireState,
                                                                    transfer.TransferId, transfer.StartCommandId)),
                ];
        }
    }
}
