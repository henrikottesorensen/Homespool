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

    /// <summary>
    /// What is on the drive, so <c>SEND_FILE_INFO</c> has something to answer with. Seed it before
    /// connecting; a completed transfer adds its own file, so sending and then listing agree.
    /// </summary>
    public FakeStorage Storage { get; } = new();

    /// <summary>The next transfer id to hand out. Firmware's come from the transfer slot; any
    /// increasing sequence is as good, and a predictable one makes tests readable.</summary>
    private int _nextTransferId = 1;

    /// <summary>The next job id. Firmware's comes from <c>marlin_vars().job_id</c>; same reasoning as
    /// <see cref="_nextTransferId"/> - any increasing sequence will do.</summary>
    private int _nextJobId = 1;

    /// <summary>
    /// Free space on <c>/usb</c>, as <c>INFO</c>'s storage block reports it. Defaults to the figure a
    /// real Core One reported (63.7 GB).
    /// </summary>
    /// <remarks>
    /// Settable because the queue's loop is supposed to check this before pushing a file, and a
    /// constant makes that check untestable - pipelining means files land ahead of the print and
    /// nothing removes them, so a queue running for a week fills a stick that a one-at-a-time workflow
    /// never would (<c>notes/print-queue.md</c>).
    /// </remarks>
    public long FreeSpace { get; set; } = 63729893376;

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
    public FakeTransfer? TryBeginTransfer(string hash,
                                          ulong teamId,
                                          string path,
                                          long totalSize,
                                          uint startCommandId,
                                          FakeDownloadOrder? order = null,
                                          Func<uint>? fileIdSource = null)
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

    /// <summary>
    /// The state half of a <c>START_PRINT</c>: begins a print if this machine will take one, and
    /// returns the new job's id - or null, which the caller renders as <c>"Can't print now"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mirrors <c>printer_state::remote_print_ready</c></b> (printer_state.cpp:530-547 at the
    /// pinned ref), which is the whole of the check <c>MarlinPrinter::start_print</c> makes before
    /// handing the path to Marlin (marlin_printer.cpp:525-545).
    /// </para>
    /// <para>
    /// <b>`Finished` and `Stopped` are accepted, and that is not an oversight.</b> Firmware really
    /// does allow a print to be started while the previous one is still sitting on the bed - it lists
    /// <c>Idle</c>, <c>Ready</c>, <c>Stopped</c> and <c>Finished</c> alike. The queue's rule that a
    /// finished printer is <i>not</i> available is therefore entirely the server's own discipline,
    /// with nothing underneath it, and a fake that refused here would hide exactly that: a loop
    /// advancing on <c>Finished</c> would fail against this double and pass against hardware, which is
    /// the worst way round. See <c>notes/print-queue.md</c>, "Firmware will start a print onto a
    /// finished part".
    /// </para>
    /// <para>
    /// One simplification: firmware can also answer <c>"Can't print now"</c> a second way, when
    /// <c>print_begin</c> is dispatched and <c>is_print_started()</c> comes back false
    /// (marlin_printer.cpp:542). That is a Marlin-side failure with no counterpart here, so this
    /// models only the state gate - the reason string a caller sees is identical either way.
    /// </para>
    /// </remarks>
    public int? TryStartPrint()
    {
        if (State is not (DeviceState.Idle or DeviceState.Ready or DeviceState.Stopped or DeviceState.Finished))
        {
            return null;
        }

        JobId = _nextJobId++;
        State = DeviceState.Printing;

        return JobId;
    }

    /// <summary>
    /// Ends the running print the way a print ends by itself, leaving the machine on the finished
    /// screen with the job still named.
    /// </summary>
    /// <remarks>
    /// The transition the whole queue turns on: a finished print <b>parks</b> until a person clears
    /// the bed and readies the printer - observed on hardware as 93 seconds in <c>FINISHED</c>
    /// (<c>notes/print-queue.md</c>). Distinct from <see cref="TryStop"/>, which is a cancellation and
    /// lands on <c>STOPPED</c> with no job.
    /// </remarks>
    public bool FinishPrint()
    {
        if (State is not (DeviceState.Printing or DeviceState.Paused))
        {
            return false;
        }

        State = DeviceState.Finished;

        return true;
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
