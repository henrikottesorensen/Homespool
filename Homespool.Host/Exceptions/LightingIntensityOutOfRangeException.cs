using System;

using Homespool.Host.Printing;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A brightness outside 0 to <see cref="PrinterLighting.MaxIntensity"/> percent.
/// </summary>
/// <remarks>
/// <b>Ours to refuse, because the printer will not.</b> Firmware stores the value in a byte without
/// checking it, so 101 comes out nearly off and -1 at about half brightness - a request it accepts
/// and answers <c>FINISHED</c>.
/// </remarks>
public class LightingIntensityOutOfRangeException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public LightingIntensityOutOfRangeException(int intensity)
        : base($"A brightness of {intensity}% is outside 0 to {PrinterLighting.MaxIntensity}%.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public LightingIntensityOutOfRangeException()
    {
    }

    public LightingIntensityOutOfRangeException(string message)
        : base(message)
    {
    }

    public LightingIntensityOutOfRangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_LightingOutOfRange";

    /// <inheritdoc />
    public object[] ResourceArguments => [PrinterLighting.MaxIntensity];
}
