namespace Homespool.Model;

/// <summary>
/// Where a printer is, coarsely — Homespool's own vocabulary, not any protocol's. Each protocol
/// maps its wire states into these values at its edge (for Prusa Connect that is
/// <c>PrinterStatusExtensions.ParseWireState</c>), and a protocol that cannot express a value
/// simply never produces it. See <c>notes/domain-vocabulary.md</c> for the mapping tables.
/// </summary>
public enum PrinterStatus
{
    /// <summary>The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>
    /// Connected, but no state has been heard yet — a real few seconds after a printer connects,
    /// not an error. Server-synthesised; no protocol puts this on the wire.
    /// </summary>
    Unknown = 1,

    /// <summary>Powered and doing nothing.</summary>
    Idle = 2,

    /// <summary>
    /// Occupied outside a print job — heating, homing, running a macro, serving its own UI.
    /// </summary>
    Busy = 3,

    /// <summary>Running a print job.</summary>
    Printing = 4,

    /// <summary>A print job is paused and resumable.</summary>
    Paused = 5,

    /// <summary>A print job ran to completion and its result is still on the bed.</summary>
    Finished = 6,

    /// <summary>A print job was cancelled before completion.</summary>
    Stopped = 7,

    /// <summary>A fault state needing intervention beyond the current job.</summary>
    Error = 8,

    /// <summary>
    /// The printer is asking for a person — a dialog, a filament prompt, a crash recovery.
    /// </summary>
    Attention = 9,

    /// <summary>
    /// Deliberately marked ready for the next job — a person's assertion that the bed is clear,
    /// never inferred. On Prusa Connect the printer owns this state; a protocol without the
    /// concept has it owned by Homespool instead.
    /// </summary>
    Ready = 10,

    /// <summary>
    /// Kept for parity with Connect's <c>Printer-read.state</c> app vocabulary and currently
    /// unreachable: no writer exists, Buddy's <c>DeviceState</c> has no such value, and
    /// <c>ParseWireState</c> — the only writer of a live status — never produces it. Do not
    /// expect it in data.
    /// </summary>
    Manipulating = 11,

    /// <summary>
    /// Not connected. A server verdict about the connection, not a report from the printer;
    /// no protocol puts this on the wire.
    /// </summary>
    Offline = 12,
}
