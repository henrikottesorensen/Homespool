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
/// already vets line by line - see <c>notes/gcode-allowlist.md</c>.
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
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.Name,
                                                       "No Prusa Connect command exists for this intent."),
        };
    }
}
