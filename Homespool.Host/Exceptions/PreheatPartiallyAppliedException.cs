using System;
using System.Globalization;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The bed's target was set and the nozzle's was not.
/// </summary>
/// <remarks>
/// <b>Exists so the partial state cannot be silently collapsed into "it failed".</b> The two heaters
/// are two commands, and the second can fail after the first has been acted on. A caller told only
/// that preheating failed would reasonably walk away from a printer that is now heating its bed.
/// </remarks>
public class PreheatPartiallyAppliedException : Exception
{
    public PreheatPartiallyAppliedException()
    {
    }

    public PreheatPartiallyAppliedException(string message)
        : base(message)
    {
    }

    public PreheatPartiallyAppliedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PreheatPartiallyAppliedException(int bedTemperature, Exception innerException)
        : base(BuildMessage(bedTemperature), innerException)
    {
        BedTemperature = bedTemperature;
    }

    /// <summary>The bed target that was applied, in degrees Celsius. Zero means it was switched off.</summary>
    public int BedTemperature { get; }

    private static string BuildMessage(int bedTemperature)
    {
        return bedTemperature == 0
            ? "The bed was switched off, but the nozzle did not answer - it may still be hot."
            : "The bed is heating to "
              + bedTemperature.ToString(CultureInfo.InvariantCulture)
              + " °C, but the nozzle did not answer and is not heating.";
    }
}
