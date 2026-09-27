using System;

using Homespool.Host.Printing;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Translates a domain <see cref="IPrinterIntent"/> into the Prusa Connect command that carries it
/// - the command-path sibling of <see cref="PrusaEventWireMapping"/>, and a written-out table for
/// the same reason: the two vocabularies correspond today, and nothing may depend on that staying
/// mechanical. An intent this protocol cannot express throws, loudly, rather than being dropped.
/// </summary>
/// <remarks>
/// The gcode allowlist is preserved by construction: no intent carries gcode, and
/// <see cref="Printing.SetTemperatures"/> translates to the composing command the allowlist
/// already vets line by line.
/// </remarks>
public static class PrusaIntentTranslator
{
    /// <summary>The wire command for <paramref name="intent"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// No Prusa Connect command expresses this intent. Reaching this is a programming error today -
    /// every intent in the vocabulary has a Prusa command - but the throw is what keeps a future
    /// intent from being silently unsendable to this protocol.
    /// </exception>
    public static Commands.ISendableCommand ToCommand(IPrinterIntent intent)
    {
        return intent switch
        {
            Printing.StartPrint p => new Commands.StartPrint { Path = p.Path },
            Printing.StopPrint => new Commands.StopPrint(),
            Printing.PausePrint => new Commands.PausePrint(),
            Printing.ResumePrint => new Commands.ResumePrint(),
            Printing.SetPrinterReady => new Commands.SetPrinterReady(),
            Printing.CancelPrinterReady => new Commands.CancelPrinterReady(),
            Printing.SetPrinterIdle => new Commands.SetPrinterIdle(),
            Printing.SetTemperatures t => new Commands.SetTemperatures(t.NozzleTemperature, t.BedTemperature),
            Printing.CancelObject c => new Commands.CancelObject { Id = ObjectId(c.ObjectId) },
            Printing.UncancelObject u => new Commands.UncancelObject { Id = ObjectId(u.ObjectId) },
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.Name,
                                                       "No Prusa Connect command exists for this intent."),
        };
    }

    /// <summary>
    /// An object id as the wire types it, checked against firmware's ceiling on the way.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Negative, or at or above <see cref="PrusaConnectConstants.MaxCancellableObjects"/>. Thrown
    /// rather than clamped: an id this application did not read off the printer is a defect, and
    /// cancelling some other object instead would hide it.
    /// </exception>
    private static ushort ObjectId(int objectId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(objectId, PrusaConnectConstants.MaxCancellableObjects);

        return (ushort)objectId;
    }
}
