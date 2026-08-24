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

    /// <summary>
    /// Which sentence a reader gets, and the reason-less <c>Rejected</c> is the interesting one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <c>Rejected</c> with no reason can mean exactly one thing.</b> Every rejection site in
    /// firmware's <c>planner.cpp</c> passes a string except the <c>background_command.has_value()</c>
    /// early return (<c>:1093-1098</c>), which constructs the event with no reason <em>by
    /// construction</em>. And it is not merely first: the outer <c>Planner::command(Command)</c>
    /// returns at that guard <b>before dispatching to any type-based overload</b>, so
    /// <i>"Processing other command"</i> (<c>:679-681</c>) is structurally unreachable while a
    /// background command is held - it answers a <c>ProcessingOtherCommand</c> the parser produced
    /// when it could not take the buffer, which is a different condition entirely.
    /// </para>
    /// <para>
    /// <b>So the empty string is load-bearing rather than missing information</b>, and the sentence
    /// says so plainly rather than hedging. The one thing that would make it a guess is a second
    /// protocol: this reads Buddy's implementation, and nothing else speaks for a printer that is not
    /// one. If that day comes, this is the decision to revisit - <c>notes/buddy-rig.md</c> carries
    /// the wire fact independently of the bug that exposed it.
    /// </para>
    /// <para>
    /// <b>Only for <c>Rejected</c>.</b> A reason-less <c>Failed</c> says nothing about a busy
    /// printer, and borrowing the sentence would invent an explanation.
    /// </para>
    /// </remarks>
    public string ResourceKey => Reason is not null ?
        "Error_PrinterRefused" :
        EventType == PrinterEventType.Rejected ? "Error_PrinterRefusedBusy" : "Error_PrinterRefusedEvent";

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
    public object[] ResourceArguments => Reason is not null ?
        [Reason] :
        EventType == PrinterEventType.Rejected ? [] : [EventType.ToString()];
}
