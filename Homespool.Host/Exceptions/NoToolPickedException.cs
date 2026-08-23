using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A multi-tool printer has no tool picked, so a gcode command carrying no <c>T</c> would not reach a
/// hotend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refused rather than sent, because sending it fails silently.</b> <c>M104</c> and <c>M702</c>
/// resolve their target from whichever tool is picked, report to the <em>serial console</em> when
/// none is, and return having done nothing — while the printer answers the frame <c>Accepted</c>, so
/// nothing on the wire says anything went wrong.
/// </para>
/// <para>
/// <b>For the heaters it is worse than doing nothing.</b> <c>M140</c> is the bed and a bed has no
/// tool, so it applies regardless: preheating would heat the bed and not the nozzle, and cooling
/// would leave the nozzle hot while reporting both heaters off. <c>notes/toolchangers.md</c> §3d.
/// </para>
/// <para>
/// <b>This is a hardware condition, not a state one</b>, which is why it is separate from
/// <see cref="PrinterBusyException"/> and why <see cref="Printing.PhysicalChangeRules"/> — an
/// allow-set over <c>PrinterStatus</c> — is not where it belongs. A printer can be perfectly idle
/// and still have nothing picked; on a toolchanger that is the resting state.
/// </para>
/// </remarks>
public class NoToolPickedException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public NoToolPickedException(int printerId)
        : base($"Printer {printerId} has no tool picked, so a command without a tool would reach nothing.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public NoToolPickedException()
    {
    }

    public NoToolPickedException(string message)
        : base(message)
    {
    }

    public NoToolPickedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_NoToolPicked";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
