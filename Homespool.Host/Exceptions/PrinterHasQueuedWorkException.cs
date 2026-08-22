using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer is marked ready with work queued, so the queue may start a print at any moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ready is the one allowed state that is also a standing instruction to start printing.</b>
/// <c>QueueRules.IsAvailable</c> is <c>status == Ready</c>, so the loop picks the head up within
/// about a second of finding one. Taking the filament out of a printer in that position means a
/// print that runs to completion extruding nothing, and <b>the failure is silent</b> - no refusal
/// anywhere, just a wasted job and a bed of air.
/// </para>
/// <para>
/// <b>This is why unloading is not simply preheating's guard reused.</b> Preheating a Ready printer
/// is harmless, because a print sets its own temperatures on the way in; there is no equivalent
/// recovery for filament that is not there. The narrower rule is unloading's alone, which is why it
/// sits in <see cref="PrusaConnect.PrinterFilamentService"/> rather than in
/// <see cref="Printing.PhysicalChangeRules"/>.
/// </para>
/// <para>
/// The remedy is a person's choice rather than something to work around: clear the queue, or unload
/// once the print in front of it has run. Both are on this page.
/// </para>
/// </remarks>
public class PrinterHasQueuedWorkException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public PrinterHasQueuedWorkException(int printerId)
        : base($"Printer {printerId} is ready with work queued, so a print could start during the unload.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public PrinterHasQueuedWorkException()
    {
    }

    public PrinterHasQueuedWorkException(string message)
        : base(message)
    {
    }

    public PrinterHasQueuedWorkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_PrinterHasQueuedWork";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
