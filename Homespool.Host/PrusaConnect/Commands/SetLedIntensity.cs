using System;
using System.Collections.Generic;

using Homespool.Host.Printing;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Sets the brightness of the printer's LED strips - <c>SET_VALUE</c> with the one kwarg
/// <c>chamber.led_intensity</c> (<c>command.cpp:419</c>, <c>planner.cpp:1053</c> at <c>v6.10.1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A command of its own rather than <see cref="SetValue"/> made sendable.</b> <c>SET_VALUE</c> is a
/// dozen settings under one wire name, each parsed into a different C type, and firmware rejects a
/// mismatched type rather than coercing it - so a general command carrying an <c>object</c> would lose
/// the one thing that has to be exact.
/// </para>
/// <para>
/// <b>Firmware does not check the range, and wraps.</b> It parses the value as an <c>int8_t</c> and
/// stores <c>(uint8_t)value * 255 / 100</c> in a byte: 101 comes out nearly off and -1 at about half.
/// So the range is this application's to enforce, and this type enforces it - see <see cref="For"/>.
/// </para>
/// <para>
/// <b>Answered <c>FINISHED</c> at once</b>, in any printer state: the handler is synchronous and has no
/// state gate. The setting is written to the printer's configuration store, so it survives a restart -
/// and is a write to that store on every send.
/// </para>
/// <para>
/// <b>A build without the strips does not know the kwarg</b> and answers <i>"Missing or broken
/// parameters"</i>; the XL and the CORE One builds have them.
/// </para>
/// </remarks>
public class SetLedIntensity : ISendableCommand
{
    private SetLedIntensity(sbyte intensity)
    {
        Intensity = intensity;
    }

    /// <summary>Brightness in percent, 0 to 100. An <see cref="sbyte"/>, because firmware parses an <c>int8_t</c>.</summary>
    public sbyte Intensity { get; }

    public string WireName => "SET_VALUE";

    /// <summary>The one kwarg, its name dotted as the telemetry block it is read back from.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["chamber.led_intensity"] = Intensity,
    };

    /// <summary>The command setting the strips to <paramref name="percent"/>.</summary>
    /// <remarks>
    /// <b>The range is why the constructor is private.</b> Nothing downstream catches a value outside
    /// it - firmware answers <c>FINISHED</c> and stores the wrapped byte - so every instance is made
    /// here, in range, whoever makes it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Negative, or above <see cref="PrinterLighting.MaxIntensity"/>. Thrown rather than clamped:
    /// callers refuse these with a sentence before getting here, so reaching this is a defect.
    /// </exception>
    public static SetLedIntensity For(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, PrinterLighting.MaxIntensity);

        return new SetLedIntensity((sbyte)percent);
    }
}
