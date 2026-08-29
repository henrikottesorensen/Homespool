using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Whether a printer's stored reason may be shown, and which of the two sources answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and separate from the page, for the reason <see cref="Homespool.Host.Queue.QueueRules"/>
/// is:</b> the decision has two sources and a gate, and being wrong about it is quiet - a sentence
/// under the wrong badge reads as fact. A function with no dependencies can be tested over every
/// state the wire can report; the page model it is called from takes fourteen services and cannot.
/// </para>
/// <para>
/// <b>The gate is a status check, and it is not redundant.</b> The reason is lifted from
/// <c>STATE_CHANGED</c> events while the status word can also arrive by telemetry, so the two can
/// briefly disagree about whether a dialog is still up. Reading the stored reason alone would put
/// the last attention's sentence under a "Printing" badge.
/// </para>
/// </remarks>
public static class AttentionRules
{
    /// <summary>
    /// The sentence to show for a printer that is waiting, or null when it is not waiting or did
    /// not explain itself.
    /// </summary>
    /// <param name="status">What the printer last reported it was doing.</param>
    /// <param name="code">The stored error code, prefix included, as the printer reported it.</param>
    /// <param name="text">
    /// The printer's own sentence, on the rare dialog that carries one. Preferred over decoding
    /// <paramref name="code"/>: a catalogue can be out of date about a machine, and the machine
    /// cannot.
    /// </param>
    public static string? Reason(PrinterStatus? status, int? code, string? text)
    {
        if (status is not (PrinterStatus.Attention or PrinterStatus.Error))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? PrinterErrorText.For(code) : text;
    }
}
