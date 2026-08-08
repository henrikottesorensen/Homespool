namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// A command carried as a gcode frame rather than a JSON one - firmware's <c>G</c> type, whose body
/// is the gcode line itself instead of a <c>{"command": …}</c> document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every implementation composes its own line from typed values.</b> None of them accepts gcode
/// from a caller, and <see cref="GcodeAllowList"/> is enforced on the way out regardless, so a
/// defect in one of them cannot widen what this application is able to say.
/// </para>
/// <para>
/// <b><c>G</c> rather than <c>F</c>.</b> Firmware understands both, and <c>F</c> means "forced" -
/// intended to be the one accepted during a print, with plain <c>G</c> refused. That distinction is
/// <em>not implemented</em> in firmware (<c>connect.cpp</c>: <i>"We don't have that distinction
/// implemented"</i>, with a TODO), so the printer will run either one mid-print and the caller's own
/// refusal is the only guard there is. Sending <c>G</c> states the intent that this is not forced,
/// and costs nothing if firmware ever grows the check.
/// </para>
/// </remarks>
public interface ISendableGcodeCommand : ISendableCommand
{
    /// <summary>
    /// The gcode line, without terminator. Must satisfy <see cref="GcodeAllowList.IsAllowed"/>;
    /// <see cref="CommandWireEncoder"/> refuses to encode it otherwise.
    /// </summary>
    string Line { get; }
}
