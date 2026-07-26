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
    public FirmwareFaithfulPolicy(PrinterIdentity identity, TimeProvider? time = null)
    {
        _identity = identity;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// How long a gcode background command stays "processing" - the window in which other commands
    /// are rejected with "Processing other command" and a resend of the same id is re-Accepted.
    /// </summary>
    public TimeSpan GcodeExecutionTime { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        if (frame.Kind is ServerCommandKind.Debug or ServerCommandKind.TransferChunk)
        {
            // 'D' is logged and thrown away (connect.cpp:411-419 vicinity of receive_command's
            // switch); a stray 'T' with no transfer active has no answer path in v1 - the transfer
            // state machine arrives with the transfer feature.
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

        return [AnswerJson(frame, device)];
    }

    private static PlannedReply Reply(byte[] payload)
    {
        return new PlannedReply(payload);
    }

    private static PlannedReply Reject(uint commandId, FakeDevice device, string reason)
    {
        return new PlannedReply(EventMessageBuilder.Build("REJECTED", device.WireState, commandId, reason));
    }

    private static PlannedReply JobControl(uint commandId, FakeDevice device, bool succeeded, string rejectReason)
    {
        // The JC macro (planner.cpp:691-702): Finished on success, Rejected with the fixed reason
        // otherwise. The ack renders the *post-transition* state, and Finished carries job_id when
        // a job still exists (render.cpp:271, has_extra).
        if (!succeeded)
        {
            return Reject(commandId, device, rejectReason);
        }

        return Reply(EventMessageBuilder.Build("FINISHED", device.WireState, commandId, jobId: device.JobId));
    }

    private PlannedReply AnswerJson(ServerCommandFrame frame, FakeDevice device)
    {
        string? name = frame.TryGetJsonCommandName();

        if (name is null)
        {
            // command.cpp:429-431 for garbage, and UnknownCommand -> "Unknown command"
            // (planner.cpp:667-669) when the JSON parses but names nothing we know.
            string reason = frame.PayloadIsValidJson() ? "Unknown command" : "Error parsing JSON";

            return Reject(frame.CommandId, device, reason);
        }

        switch (name)
        {
            case "PAUSE_PRINT":
                return JobControl(frame.CommandId, device, device.TryPause(), "No print to pause");

            case "RESUME_PRINT":
                return JobControl(frame.CommandId, device, device.TryResume(), "No paused print to resume");

            case "STOP_PRINT":
                return JobControl(frame.CommandId, device, device.TryStop(), "No print to stop");

            case "SET_PRINTER_READY":
                // planner.cpp:772-776 - STATE_CHANGED on success, not FINISHED.
                return device.TrySetReady()
                    ? Reply(EventMessageBuilder.Build("STATE_CHANGED", device.WireState, frame.CommandId))
                    : Reject(frame.CommandId, device, "Can't set ready now");

            case "CANCEL_PRINTER_READY":
                // planner.cpp:778-784 - un-readying cannot fail.
                device.CancelReady();

                return Reply(EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId));

            case "SET_PRINTER_IDLE":
                // planner.cpp:786-790 + marlin_printer.cpp:579-586.
                return device.TrySetIdle()
                    ? Reply(EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId))
                    : Reject(frame.CommandId, device, "Can't set idle now");

            case "SEND_INFO":
                // planner.cpp:735-740 - the INFO event, carrying the command id.
                return Reply(EventMessageBuilder.BuildInfo(_identity, device.WireState, frame.CommandId, device.JobId));

            default:
                return Reject(frame.CommandId, device, "Unknown command");
        }
    }
}
