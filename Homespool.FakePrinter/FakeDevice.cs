using System;
using System.Collections.Generic;

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
    /// What the running job is printing, so <c>SEND_JOB_INFO</c> can name it. Null when no job
    /// exists, and null again once one ends.
    /// </summary>
    /// <remarks>
    /// <b>The only thing a printer will tell you about whose print is running</b>, which is why the
    /// fake has to carry it: telemetry has a <c>job_id</c> and no file name, so a server working out
    /// whether a print is its own has nothing else to go on. Cleared when the job ends because
    /// firmware's own answer for a remembered job renders a state and no path - see
    /// <see cref="EventMessageBuilder.BuildJobInfo"/>.
    /// </remarks>
    public string? JobPath { get; private set; }

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
    /// never would.
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

    /// <summary>
    /// Whether this machine will accept remote work right now - <c>printer_state::remote_print_ready</c>
    /// (printer_state.cpp:561-577), which answers true for exactly <c>Idle</c>, <c>Ready</c>,
    /// <c>Stopped</c> and <c>Finished</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One predicate because firmware has one</b>: <c>start_print</c> (marlin_printer.cpp:526-528)
    /// and <c>set_printer_ready</c> (marlin_printer.cpp:579-582) both gate on this same call, so a
    /// fake with two independent state lists could drift into accepting a print where it refused the
    /// flag - a disagreement no real printer can produce.
    /// </para>
    /// <para>
    /// <b>Two of those four are the interesting ones.</b> <c>Stopped</c> and <c>Finished</c> both
    /// have a part still on the bed, and firmware takes work in both regardless - which is why the
    /// queue's own gate is narrower than this and has nothing underneath it. Being faithful here is
    /// what keeps that gate honestly tested.
    /// </para>
    /// <para>
    /// <b>No counterpart to the preview arm.</b> The real predicate takes a <c>preview_only</c> flag
    /// and short-circuits true for the two print-preview states, which this device has no notion of.
    /// Nothing here can reach that arm, so it is absent rather than approximated.
    /// </para>
    /// </remarks>
    private bool RemotePrintReady =>
        State is DeviceState.Idle or DeviceState.Ready or DeviceState.Stopped or DeviceState.Finished;

    /// <summary>Puts the device into a printing state with the given job - test/scenario setup.</summary>
    public void StartPrint(int jobId, string? path = null)
    {
        JobId = jobId;
        JobPath = path;
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
    /// the worst way round.
    /// </para>
    /// <para>
    /// One simplification: firmware can also answer <c>"Can't print now"</c> a second way, when
    /// <c>print_begin</c> is dispatched and <c>is_print_started()</c> comes back false
    /// (marlin_printer.cpp:542). That is a Marlin-side failure with no counterpart here, so this
    /// models only the state gate - the reason string a caller sees is identical either way.
    /// </para>
    /// </remarks>
    /// <param name="path">What it is printing, so the job can be named when asked about.</param>
    public int? TryStartPrint(string? path = null)
    {
        if (!RemotePrintReady)
        {
            return null;
        }

        JobId = _nextJobId++;
        JobPath = path;
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
    /// on hardware. Distinct from <see cref="TryStop"/>, which is a cancellation and
    /// lands on <c>STOPPED</c> with no job.
    /// </remarks>
    public bool FinishPrint()
    {
        if (State is not (DeviceState.Printing or DeviceState.Paused))
        {
            return false;
        }

        State = DeviceState.Finished;

        // The id survives - the finished screen still names the job - but the path does not, because
        // firmware's answer for a job it merely remembers renders a FIN_OK state and nothing else.
        JobPath = null;

        return true;
    }

    /// <summary>Forces an arbitrary state - test/scenario setup (e.g. Attention, Error).</summary>
    public void ForceState(DeviceState state)
    {
        State = state;
    }

    /// <summary>
    /// Events the device wants to send of its own accord, ahead of the next telemetry message.
    /// </summary>
    /// <remarks>
    /// <b>A printer talks without being asked.</b> Everything the fake sent until now was either
    /// telemetry on a timer or an answer to a command, so a state change it decided on itself - the
    /// shape of every attention - had nowhere to come from. Drained by
    /// <see cref="FakePrinterClient"/> before each telemetry message, on both transports.
    /// </remarks>
    public Queue<byte[]> PendingEvents { get; } = new();

    /// <summary>
    /// Reports an attention with a code, the way a runout or a preview question does: the state
    /// changes and a <c>STATE_CHANGED</c> carrying the code goes out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The code is where the reason lives, and the only place.</b> Telemetry repeats
    /// <c>dialog_id</c> for the duration but carries no code, so a fake that only changed
    /// <see cref="State"/> would reproduce a printer asking for help and refusing to say why -
    /// which is exactly the thing a server cannot then be tested against.
    /// </para>
    /// <para>
    /// <paramref name="text"/> is the red-screen shape (firmware's <c>ErrorPrinter</c> fills words
    /// in; an ordinary attention leaves them out), so it defaults to absent.
    /// </para>
    /// </remarks>
    /// <param name="code">The five-digit code, model prefix included, as firmware spells it.</param>
    /// <param name="text">The printer's own sentence, on the dialogs that carry one.</param>
    /// <param name="state">The state to enter; <c>Attention</c> unless an error is being modelled.</param>
    public void ReportAttention(int code, string? text = null, DeviceState state = DeviceState.Attention)
    {
        State = state;
        DialogId = unchecked(DialogId + 1);

        PendingEvents.Enqueue(EventMessageBuilder.BuildStateChanged(WireState, code, text, DialogId, JobId));
    }

    /// <summary>The dialog identifier reported alongside an attention; increments per dialog.</summary>
    public uint DialogId { get; private set; }

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
        JobPath = null;

        return true;
    }

    /// <summary>
    /// Set ready: <c>set_printer_ready(true)</c> gates on the same
    /// <see cref="RemotePrintReady"/> as starting a print does
    /// (<c>MarlinPrinter::set_printer_ready</c>, marlin_printer.cpp:579-586), so a printing, paused
    /// or attending machine refuses with <c>"Can't set ready now"</c> and a finished one accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Accepting from <c>Finished</c> and <c>Stopped</c> is the point of the gate, not an
    /// edge case.</b> Firmware's flag <em>overrides</em> those two screens - it is exactly "a human
    /// cleared the bed and said carry on", which is how a parked print is released. A fake that took
    /// the flag only from <c>Idle</c> refused what hardware accepts, so a test covering the ordinary
    /// way a queue resumes would have failed against the double and passed against a real printer.
    /// </para>
    /// <para>
    /// <b>The job is dropped, as it is when leaving that screen for idle</b> - see
    /// <see cref="TrySetIdle"/>. Firmware's <c>has_job</c> is false in <c>Ready</c>, so it stops
    /// sending the job block entirely; keeping a job id here would put a printer on the wire that
    /// claims to be ready for work and to have work in hand at the same time.
    /// </para>
    /// <para>
    /// <b>Idempotent from <c>Ready</c></b>, because firmware only asks whether the flag <i>may</i> be
    /// raised and <c>Ready</c> answers yes - re-readying a ready printer is not a refusal.
    /// </para>
    /// </remarks>
    public bool TrySetReady()
    {
        if (!RemotePrintReady)
        {
            return false;
        }

        State = DeviceState.Ready;
        JobId = null;
        JobPath = null;

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
        JobPath = null;

        return true;
    }
}
