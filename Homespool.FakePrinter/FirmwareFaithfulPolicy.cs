using System;
using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Answers commands the way the firmware's planner does
/// (Prusa-Firmware-Buddy <c>planner.cpp:660-800, 1075-1112</c> at the pinned ref): the quick
/// job-control commands answer <c>FINISHED</c> or <c>REJECTED</c> directly per the device state,
/// gcode runs as a background command (<c>ACCEPTED</c> now, <c>FINISHED</c> later, other commands
/// rejected as busy in between), a repeated command id is refused, and <c>SEND_INFO</c> yields an
/// <c>INFO</c> event carrying the command id.
/// </summary>
public sealed class FirmwareFaithfulPolicy : CommandAnswerPolicy
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
            _backgroundDoneAt = _time.GetTimestamp()
                                + (long)(GcodeExecutionTime.TotalSeconds * _time.TimestampFrequency);

            return
            [
                Reply(EventMessageBuilder.Build("ACCEPTED", device.WireState, frame.CommandId)),
                new PlannedReply(
                    EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId),
                    GcodeExecutionTime),
            ];
        }

        return AnswerJson(frame, device);
    }

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
        return FakeTransfer.IsPlainGcode(path)
               || path.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bgc", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bbf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the transfer's next range request, if it wants one. Silence here is correct rather than
    /// lazy: a request is only re-armed once the previous segment is fully delivered.
    /// </summary>
    private static void AppendRequest(List<PlannedReply> replies, FakeTransfer transfer)
    {
        if (transfer.NextRequest() is { } request)
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
    /// <c>SET_PRINTER_IDLE</c> are untested either way and left reporting the new state, on the same
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

            case "SET_PRINTER_IDLE":
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

            case "START_PRINT":
                return StartPrint(frame, device);

            case "SEND_JOB_INFO":
                return SendJobInfo(frame, device);

            case "SEND_FILE_INFO":
                // planner.cpp:751-759 - the path is checked before anything is rendered, and a path
                // outside /usb is refused rather than answered.
                return SendFileInfo(frame, device);

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
    /// Accepts a download and opens the negotiation: <c>TRANSFER_INFO</c> - <b>not</b>
    /// <c>FINISHED</c> - followed immediately by the first range request.
    /// </summary>
    /// <remarks>
    /// The rejection arms are <c>handle_transfer_result</c>'s (planner.cpp:801-824), each with its
    /// machine-readable code. The ordering matters: firmware checks the path before it tries to take
    /// the transfer slot (<c>init_transfer</c>, planner.cpp:209-221 runs before
    /// <c>Transfer::begin</c>), so a bad path is refused even while another transfer is running.
    /// </remarks>
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
    /// <b>Only four reasons are reachable</b> - <c>Forbidden path</c>, <c>File not found</c>,
    /// <c>Can't print now</c> and <c>Tools mapping not enabled</c>. <c>File is busy</c> and
    /// <c>File is being transferred</c> belong to <c>delete_file</c> and are deliberately not sent
    /// here; a server waiting on either as a busy signal would wait for something firmware cannot
    /// produce.
    /// </para>
    /// <para>
    /// A file still arriving is <b>not</b> refused: <c>is_valid_file_or_transfer</c> accepts a partial
    /// transfer, so this looks the file up in storage exactly as <c>SEND_FILE_INFO</c> does and lets
    /// the state gate decide. That is why the fake starts a print on a file whose transfer is still
    /// running, which is what hardware does.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PlannedReply> StartPrint(ServerCommandFrame frame, FakeDevice device)
    {
        string? path = PathArgument.TryParse(frame.Payload);

        if (path is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!path.StartsWith(FakeStorage.Root + "/", StringComparison.Ordinal)
            || path.Contains("/../", StringComparison.Ordinal))
        {
            // Stricter than SEND_FILE_INFO's check by exactly one case: /usb itself is a directory,
            // and printing a directory is not a path this command has any meaning for.
            return [Reject(frame.CommandId, device, "Forbidden path")];
        }

        FakeStorageEntry? entry = device.Storage.Find(path);

        if (entry is null || entry.IsFolder)
        {
            return [Reject(frame.CommandId, device, "File not found")];
        }

        if (device.TryStartPrint(path) is not { } jobId)
        {
            return [Reject(frame.CommandId, device, "Can't print now")];
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
        if (JobIdArgument.TryParse(frame.Payload) is not { } jobId)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (device.JobId is not { } current)
        {
            return [Reject(frame.CommandId, device, "No job in progress")];
        }

        if (current != jobId)
        {
            return [Reject(frame.CommandId, device, "Job ID doesn't match")];
        }

        // A job with no path is one the machine is only remembering - the finished screen. It
        // answers, and says nothing that identifies the file.
        return device.JobPath is { } path ?
            [Reply(EventMessageBuilder.BuildJobInfo(device.WireState, current, path, "PRINTING", frame.CommandId))] :
            [Reply(EventMessageBuilder.BuildJobInfo(device.WireState, current, null, "FIN_OK", frame.CommandId))];
    }

    private IReadOnlyList<PlannedReply> SendFileInfo(ServerCommandFrame frame, FakeDevice device)
    {
        string? path = PathArgument.TryParse(frame.Payload);

        if (path is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        bool onUsb = path.StartsWith(FakeStorage.Root + "/", StringComparison.Ordinal)
                     || string.Equals(path, FakeStorage.Root, StringComparison.Ordinal);

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
                                                        frame.CommandId))
            ];
    }

    private IReadOnlyList<PlannedReply> StartDownload(ServerCommandFrame frame, FakeDevice device)
    {
        StartDownloadArguments? arguments = StartDownloadArguments.TryParse(frame.Payload);

        if (arguments is null)
        {
            return [Reject(frame.CommandId, device, "Missing or broken parameters")];
        }

        if (!arguments.Path.StartsWith("/usb/", StringComparison.Ordinal)
            || arguments.Path.Contains("/../", StringComparison.Ordinal))
        {
            // path_allowed, planner.cpp:135-141.
            return [RejectWithCode(frame.CommandId, device, "Not allowed outside /usb", "STORAGE_FAILURE")];
        }

        if (!IsTransferrable(arguments.Path))
        {
            // filename_is_transferrable - printable formats plus firmware images (filename_type.cpp).
            return [RejectWithCode(frame.CommandId, device, "Unsupported file type", "STORAGE_FAILURE")];
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
    /// that now exists. Real firmware can emit that <c>FILE_INFO</c> more than once and as early as
    /// mid-transfer (<c>notify_created</c> fires from a backup checkpoint once the partial file is
    /// printable, transfer.cpp:262); reproducing that timing would mean modelling the backup intervals
    /// and the download order's validity rules, so this emits exactly one, at the end. A documented
    /// simplification, like the ping cadence in <see cref="FakePrinterClient"/>.
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
                AppendRequest(replies, transfer);

                return replies;

            case ChunkOutcome.Completed:
                device.EndTransfer();

                // The file is now on the drive, so a later SEND_FILE_INFO finds it. Without this the
                // fake would report a transfer finishing and then deny the file exists, which is the
                // kind of incoherence that makes an end-to-end test prove nothing.
                long completedAt = _time.GetUtcNow().ToUnixTimeSeconds();

                device.Storage.AddFile(transfer.Path, transfer.TotalSize, completedAt);

                return
                [
                    Reply(EventMessageBuilder.BuildTransferTerminal("TRANSFER_FINISHED", device.WireState,
                                                                    transfer.TransferId, transfer.StartCommandId)),
                    Reply(EventMessageBuilder.BuildFileInfo(device.WireState, transfer.Path, transfer.TotalSize,
                                                            completedAt)),
                ];

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
