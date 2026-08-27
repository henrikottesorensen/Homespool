using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Model;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Preheating and cooling a printer: the paired nozzle-and-bed operations the web UI offers, over the
/// two individually settable commands underneath.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pairing lives here rather than in a command</b> so that the commands stay what the printer
/// takes - one heater each. A caller in the code may set either alone; nothing on a page can, which
/// is what stops a printer being left with one heater on and the other off by accident.
/// </para>
/// <para>
/// <b>Reachable from the web UI only, and that is a security decision rather than a scoping
/// accident.</b> Personal access tokens are deliberately unscoped - no scopes and no expiry,
/// because that way lies a badly-implemented JWT - so
/// any token is full authority over everything <c>/api/v1</c> offers. **The printer's own idle
/// cutoff is no defence against one**, because reissuing the command before it expires resets it: a
/// leaked token would mean a nozzle held at maximum indefinitely.
/// </para>
/// <para>
/// That cutoff is firmware's safety timer, and it is worth knowing exactly rather than
/// approximately: <b>30 minutes on a printer with a screen, 10 without</b>
/// (<c>src/feature/safety_timer/safety_timer.hpp:36-40</c>, on <c>HAS_HUMAN_INTERACTIONS()</c>) -
/// and <b>settable</b>, with a 3-second floor and no ceiling
/// (<c>safety_timer.cpp:43</c>). So it is neither a constant nor a guarantee, which is a second
/// reason not to lean on it.
/// </para>
/// <para>
/// That is a different argument from the one that settled token scoping, and worth keeping
/// separate. Everything an unscoped token can do today is <em>data</em> harm - cancel prints, delete
/// files, queue rubbish. This would be the first capability with a <b>physical</b> consequence that
/// persists after the holder stops paying attention. Exposing it on the API needs either scoped
/// tokens or a heating duration bound a reissue cannot reset; a signed-in session is a different
/// matter, being the same authority as standing at the machine.
/// </para>
/// <para>
/// <b>The state check is this application's alone</b>, and it lives in
/// <see cref="Printing.PhysicalChangeRules"/> - shared with unloading filament, which needs the same
/// rule for the same reason. Nothing downstream will second-guess what it decides.
/// </para>
/// </remarks>
public class PrinterPreheatService
{
    private readonly PrinterCommandService _commands;
    private readonly QueueSnapshotReader _snapshots;
    private readonly ToolTargetReader _tools;

    public PrinterPreheatService(PrinterCommandService commands,
                                 QueueSnapshotReader snapshots,
                                 ToolTargetReader tools)
    {
        _commands = commands;
        _snapshots = snapshots;
        _tools = tools;
    }

    /// <summary>
    /// Heats nozzle and bed to a filament's preset.
    /// </summary>
    /// <exception cref="PrinterBusyException">The printer is in a state where this would interfere.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c>.</exception>
    public Task<PreheatOutcome> PreheatAsync(int printerId,
                                             Caller caller,
                                             FilamentPreset preset,
                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return ApplyAsync(printerId, caller, preset.BedTemperature, preset.NozzleTemperature, cancellationToken);
    }

    /// <summary>
    /// Turns both heaters off.
    /// </summary>
    /// <remarks>
    /// <b>Refused mid-print for the same reason preheating is</b>, which is worth stating because the
    /// instinct runs the other way: a button that says "cooldown" looks like a safety control that
    /// ought always to work. It is not one. Cooling the nozzle during a print destroys the print
    /// without stopping it, and the control for "stop what you are doing" is Stop, which exists and
    /// tells the printer so.
    /// </remarks>
    public Task<PreheatOutcome> CooldownAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        return ApplyAsync(printerId, caller, bedTemperature: 0, nozzleTemperature: 0, cancellationToken);
    }

    private async Task<PreheatOutcome> ApplyAsync(int printerId,
                                                  Caller caller,
                                                  int bedTemperature,
                                                  int nozzleTemperature,
                                                  CancellationToken cancellationToken)
    {
        QueueSnapshot snapshot = await _snapshots.ReadAsync(printerId, cancellationToken);

        if (!PhysicalChangeRules.IsAllowed(snapshot.Status))
        {
            throw new PrinterBusyException(snapshot.Status);
        }

        // Both lines are toolless, and only one of them survives that on a toolchanger: M140 is the
        // bed and always applies, while M104 silently declines when no tool is picked. Sending the
        // pair anyway would heat the bed and not the nozzle - or, cooling, leave the nozzle hot while
        // reporting both heaters off. The pair is atomic on the wire and half-applied in the machine.
        ToolTarget target = await _tools.ReadAsync(printerId, cancellationToken);

        if (!target.ReachesAHotend)
        {
            throw new NoToolPickedException(printerId);
        }

        // One command carrying both lines. Two commands cannot work: gcode is answered Accepted when
        // it is queued rather than when it has run, so the second arrives while the first is still
        // executing and is refused with "Processing other command" - observed on a live stack before
        // this was one command.
        CommandOutcome? answer = await _commands.SendCommandAsync(
            printerId, new SetTemperatures(nozzleTemperature, bedTemperature), caller, cancellationToken);

        // The printer's own answer decides, not the fact that a frame was written. Reporting success
        // here regardless is what let a refusal be shown to a user as "both heaters switched off".
        if (answer is not null && answer.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            throw new PrinterRefusedException(answer.EventType, answer.Reason);
        }

        return new PreheatOutcome(answer);
    }
}

/// <summary>What the printer answered.</summary>
/// <param name="Answer">
/// The printer's own answer, or null for a command expecting no reply. One answer rather than two,
/// because both heaters are set by one command.
/// </param>
public sealed record PreheatOutcome(CommandOutcome? Answer);
