using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// When a physical change commanded from off-machine is somebody's intention rather than an accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only guard there is, for every caller that consults it.</b> Firmware understands a
/// "forced" gcode frame, <c>F</c>, meant to be the one accepted during a print with plain <c>G</c>
/// refused - and it does not implement the distinction (<c>src/connect/connect.cpp:490</c>: <i>"We
/// don't have that distinction implemented"</i>, with a TODO). So a printer will retarget a heater or
/// retract filament in the middle of a print and ruin it, reporting nothing wrong. Whatever this
/// application decides is the whole of the protection; nothing downstream will second-guess it.
/// </para>
/// <para>
/// <b>An allow-set, not a denylist</b> - the same reasoning as <see cref="PrusaConnect.Commands.GcodeAllowList"/>. A
/// list of forbidden states fails open on the one nobody thought of, and the enum has thirteen
/// members of which several mean "mid-something".
/// </para>
/// <para>
/// <b>One set rather than one per feature.</b> It was <c>PrinterPreheatService</c>'s own private
/// field while heating was the only thing that touched the machine this way; unloading filament is
/// the second, and two copies of a safety rule drift apart in exactly the way that leaves the newer
/// one wrong. A caller needing a <em>narrower</em> rule adds its own condition on top of this rather
/// than restating it - see <see cref="PrusaConnect.PrinterFilamentService"/>, which additionally
/// refuses a <c>Ready</c> printer with work queued.
/// </para>
/// </remarks>
public static class PhysicalChangeRules
{
    /// <summary>
    /// The states in which a change to the machine may be commanded from here.
    /// </summary>
    /// <remarks>
    /// <c>Finished</c> and <c>Stopped</c> are in because that is exactly when someone prepares for
    /// the next print. <c>Paused</c> is out: a paused print resumes, and it resumes into whatever it
    /// finds. <c>Attention</c> is out because it is frequently a filament runout <em>during</em> a
    /// print - and because it is one value covering a crash stop, a "remove print" prompt and an MMU
    /// error too, so there is no reading of it that distinguishes the safe case.
    /// </remarks>
    public static readonly IReadOnlySet<PrinterStatus> Allowed = new HashSet<PrinterStatus>
    {
        PrinterStatus.Idle,
        PrinterStatus.Ready,
        PrinterStatus.Finished,
        PrinterStatus.Stopped,
    };

    /// <summary>Whether <paramref name="status"/> is one this application will act on.</summary>
    public static bool IsAllowed(PrinterStatus status)
    {
        return Allowed.Contains(status);
    }
}
