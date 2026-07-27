using System;

namespace Homespool.FakePrinter;

/// <summary>
/// The fake's device-side state machine: what state the machine is in, and which job-control
/// transitions are legal from it. Mirrors <c>MarlinPrinter::job_control</c>
/// (Prusa-Firmware-Buddy <c>src/connect/marlin_printer.cpp:442</c> at the pinned ref) plus
/// <c>set_ready</c>/<c>set_idle</c> (<c>:574-586</c>), because whether a command answers
/// <c>FINISHED</c> or <c>REJECTED</c> turns entirely on this.
/// </summary>
/// <remarks>
/// Not thread-safe by design: it is owned by the connection's single command-processing loop, the
/// same single-owner shape the firmware's planner has (and <c>PrinterConnectionActor</c> on the
/// server side). One simplification against hardware: the real machine passes through transient
/// states (Aborting, heating phases) before settling; the fake transitions instantly, so an ack's
/// <c>state</c> field shows the settled state rather than a transient one.
/// </remarks>
public sealed class FakeDevice
{
    /// <summary>Current device state; starts <see cref="DeviceState.Idle"/> like a booted printer.</summary>
    public DeviceState State { get; private set; } = DeviceState.Idle;

    /// <summary>The running (or paused) job's id; null when no job exists.</summary>
    public int? JobId { get; private set; }

    /// <summary>
    /// The one transfer this device may have in progress, or null.
    /// </summary>
    /// <remarks>
    /// One, not a collection, because firmware's transfer slot is a single system-wide resource
    /// shared with PrusaLink uploads (<c>Monitor</c>, monitor.hpp:85-98) - a second
    /// <c>START_CONNECT_DOWNLOAD</c> is rejected with "Another transfer in progress" rather than
    /// queued. The server's <c>PrinterConnectionActor</c> models its side the same way, for the same
    /// reason.
    /// </remarks>
    public FakeTransfer? Transfer { get; private set; }

    /// <summary>The next transfer id to hand out. Firmware's come from the transfer slot; any
    /// increasing sequence is as good, and a predictable one makes tests readable.</summary>
    private int _nextTransferId = 1;

    /// <summary>
    /// Takes the transfer slot, or returns null when it is already taken.
    /// </summary>
    /// <param name="hash">The server's transfer token.</param>
    /// <param name="teamId">From the command.</param>
    /// <param name="path">Destination on this device.</param>
    /// <param name="totalSize">The command's <c>orig_size</c>.</param>
    /// <param name="startCommandId">The command id that began it.</param>
    /// <param name="order">Forces a download order; null picks it the way firmware does.</param>
    /// <param name="fileIdSource">Supplies each negotiation's <c>file_id</c>.</param>
    public FakeTransfer? TryBeginTransfer(string hash, ulong teamId, string path, long totalSize,
        uint startCommandId, FakeDownloadOrder? order = null, Func<uint>? fileIdSource = null)
    {
        if (Transfer is not null)
        {
            return null;
        }

        Transfer = new FakeTransfer(hash, teamId, path, totalSize, _nextTransferId++, startCommandId,
            order, fileIdSource);

        return Transfer;
    }

    /// <summary>
    /// The transfer that ended most recently, successful or not - kept so a test can assert on the
    /// bytes it received after the slot itself has been released.
    /// </summary>
    public FakeTransfer? LastTransfer { get; private set; }

    /// <summary>Releases the transfer slot once the transfer has ended, either way.</summary>
    public void EndTransfer()
    {
        LastTransfer = Transfer;
        Transfer = null;
    }

    /// <summary>The wire spelling of <see cref="State"/> - <c>IDLE</c>, <c>PRINTING</c>, etc.</summary>
    public string WireState => State switch
    {
        DeviceState.Idle => "IDLE",
        DeviceState.Busy => "BUSY",
        DeviceState.Printing => "PRINTING",
        DeviceState.Paused => "PAUSED",
        DeviceState.Finished => "FINISHED",
        DeviceState.Stopped => "STOPPED",
        DeviceState.Error => "ERROR",
        DeviceState.Attention => "ATTENTION",
        DeviceState.Ready => "READY",
        DeviceState.Unknown => "UNKNOWN",
        _ => throw new InvalidOperationException($"Unmapped device state {State}."),
    };

    /// <summary>Puts the device into a printing state with the given job - test/scenario setup.</summary>
    public void StartPrint(int jobId)
    {
        JobId = jobId;
        State = DeviceState.Printing;
    }

    /// <summary>Forces an arbitrary state - test/scenario setup (e.g. Attention, Error).</summary>
    public void ForceState(DeviceState state)
    {
        State = state;
    }

    /// <summary>Pause: legal only while <see cref="DeviceState.Printing"/> (job_control, Pause arm).</summary>
    public bool TryPause()
    {
        if (State != DeviceState.Printing)
        {
            return false;
        }

        State = DeviceState.Paused;

        return true;
    }

    /// <summary>Resume: legal only while <see cref="DeviceState.Paused"/> (job_control, Resume arm).</summary>
    public bool TryResume()
    {
        if (State != DeviceState.Paused)
        {
            return false;
        }

        State = DeviceState.Printing;

        return true;
    }

    /// <summary>
    /// Stop: legal from Paused, Printing or Attention (job_control, Stop arm). The job ends and the
    /// machine settles on the stopped screen.
    /// </summary>
    public bool TryStop()
    {
        if (State is not (DeviceState.Paused or DeviceState.Printing or DeviceState.Attention))
        {
            return false;
        }

        State = DeviceState.Stopped;
        JobId = null;

        return true;
    }

    /// <summary>
    /// Set ready: <c>set_printer_ready(true)</c> succeeds only from Idle - a busy or printing
    /// machine can't be marked ready for the next job.
    /// </summary>
    public bool TrySetReady()
    {
        if (State != DeviceState.Idle)
        {
            return false;
        }

        State = DeviceState.Ready;

        return true;
    }

    /// <summary>Cancel ready: un-readying cannot fail (the firmware asserts as much).</summary>
    public void CancelReady()
    {
        if (State == DeviceState.Ready)
        {
            State = DeviceState.Idle;
        }
    }

    /// <summary>
    /// Set idle: legal only from the Finished or Stopped screen (<c>MarlinPrinter::set_idle</c>,
    /// <c>marlin_printer.cpp:579-586</c> - the check the real MK3.5 demonstrated live on
    /// 2026-07-24 by rejecting with "Can't set idle now").
    /// </summary>
    public bool TrySetIdle()
    {
        if (State is not (DeviceState.Finished or DeviceState.Stopped))
        {
            return false;
        }

        State = DeviceState.Idle;
        JobId = null;

        return true;
    }
}
