using System;

using Homespool.Model;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The command reached the printer and the printer said no.
/// </summary>
/// <remarks>
/// Distinct from the command never arriving: the caller asked for something the printer declined,
/// and the printer usually says why in its own words. Reporting that as success - which happens by
/// default if nothing inspects the answer - tells someone their printer is doing something it
/// refused to do.
/// </remarks>
public class PrinterRefusedException : Exception, ILocalisableError
{
    public PrinterRefusedException()
    {
    }

    public PrinterRefusedException(string message)
        : base(message)
    {
    }

    public PrinterRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrinterRefusedException(PrinterEventType eventType, string? reason)
        : base(reason is null ? $"The printer refused it ({eventType})." : $"The printer refused it: {reason}")
    {
        EventType = eventType;
        Reason = reason;
    }

    /// <summary>The answering event - <c>Rejected</c> or <c>Failed</c>.</summary>
    public PrinterEventType EventType { get; }

    /// <summary>The printer's own words, when it gave any.</summary>
    public string? Reason { get; }

    /// <inheritdoc />
    public string ResourceKey => Reason is null ? "Error_PrinterRefusedEvent" : "Error_PrinterRefused";

    /// <summary>
    /// The printer's reason, untranslated, inside a sentence that is translated.
    /// </summary>
    /// <remarks>
    /// <b>The frame is ours and the reason is the printer's</b>, so exactly one of the two moves.
    /// Translating the reason would put words in the printer's mouth that it did not say and that no
    /// support thread would match; leaving the whole sentence English would make the refusal the one
    /// message on the page a Danish reader cannot read. The event name is treated the same way when
    /// there is no reason: it is firmware vocabulary, and appears verbatim.
    /// </remarks>
    public object[] ResourceArguments => Reason is null ? [EventType.ToString()] : [Reason];
}
