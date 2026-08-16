using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// An <see cref="ICommand"/> the server actually knows how to send, as opposed to the ~25 other
/// concrete classes in this folder that are still hollow markers awaiting the rest of the command
/// vocabulary. Narrower than <see cref="ICommand"/> on purpose, so those still-hollow classes don't
/// need a throwaway <see cref="WireName"/> implementation.
/// </summary>
public interface ISendableCommand : ICommand
{
    /// <summary>The wire string for a J-command's JSON payload. Confirmed against firmware source
    /// (Prusa-Firmware-Buddy command.cpp:149-166 at e96ce2b, the ref AGENT-NOTES.md pins).</summary>
    string WireName { get; }

    /// <summary>
    /// The command's <c>kwargs</c>, or null for the NO_ARGS commands - which is most of them, hence
    /// the default. Implementing this changes the encoded payload shape; see
    /// <see cref="CommandWireEncoder"/>.
    /// </summary>
    /// <remarks>
    /// Values are serialized as-is by <c>System.Text.Json</c>, so a member's CLR type is its wire
    /// type: firmware parses each kwarg into a fixed C type and rejects the whole command as
    /// <c>BrokenCommand</c> on a mismatch, rather than coercing.
    /// </remarks>
    IReadOnlyDictionary<string, object?>? Arguments => null;

    /// <summary>
    /// Whether the printer answers this command with an event carrying its <c>command_id</c>. True
    /// for all but a handful, hence the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command declaring <c>false</c> is completed as
    /// <see cref="Printing.CommandSendOutcome.Dispatched"/> the moment its frame is written, and never takes
    /// the one in-flight slot - because nothing will ever free it. The alternative is worse than it
    /// sounds: the caller would wait out the full response timeout and be told the command failed,
    /// for a command that did exactly what was asked.
    /// </para>
    /// <para>
    /// <b>Not a guess about slow printers.</b> This is only for commands the firmware
    /// <i>structurally</i> cannot acknowledge - where answering would require code to run after the
    /// point of no return. A merely slow answer is <see cref="Printing.CommandSendOutcome.ResponseTimedOut"/>
    /// and stays that way; do not reach for this to paper over one.
    /// </para>
    /// </remarks>
    bool ExpectsReply => true;

    /// <summary>
    /// What an account must hold to send this command. <b>Declared by the command rather than passed
    /// in by the caller</b>, so a new call site cannot pick a weaker one by accident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default is the strict answer</b>, so a command that says nothing is treated as steering
    /// the machine. Overriding it is how a command declares itself weaker, which is a decision worth
    /// making explicitly and in one visible place.
    /// </para>
    /// <para>
    /// <b><see cref="StopPrint"/> is the subtle one.</b> It declares <see cref="Capability.Print"/>,
    /// which alone would let anybody with that capability stop anybody's print - so the ownership half
    /// is enforced above, by <c>PrintStopService</c>, which every path to a stop goes through. The
    /// capability here is the floor, not the whole rule.
    /// </para>
    /// </remarks>
    Capability RequiredCapability => Capability.ControlPrinter;
}

/// <summary>
/// A command whose answer is a <i>payload</i> rather than a verdict - one that asks the printer a
/// question and gets data back, like <c>SEND_FILE_INFO</c> returning a directory listing.
/// </summary>
/// <typeparam name="TAnswer">
/// The shape of the answering event's <c>data</c>, e.g. <c>FileInfoEventDataDTO</c> for
/// <c>SEND_FILE_INFO</c>. Deserialised once, in <see cref="Printing.PrinterCommandService"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>A marker, deliberately - the type parameter is the whole declaration.</b> It is what lets
/// <see cref="Printing.PrinterCommandService.AskAsync"/> return a typed answer without anything switching on
/// event type at runtime: the command's own static type names the shape, so the compiler dispatches
/// it and each new question-asking command adds nothing to any existing method.
/// </para>
/// <para>
/// <b>Why the raw payload stops here.</b> The alternative was to hand callers the answering event's
/// <c>data</c> as a <c>JsonElement</c> and let each parse it. That works exactly until the fifth
/// caller, at which point the schema lives in five call sites and nowhere else. Above this
/// interface the payload is always a real type; the untyped form exists only between the actor and
/// the service, which is one hop and one reader.
/// </para>
/// <para>
/// Commands answered by their event type alone - <c>Finished</c> or <c>Rejected</c>, which is all
/// twelve sendable ones today - implement plain <see cref="ISendableCommand"/> and go through
/// <see cref="Printing.PrinterCommandService.SendCommandAsync(int, ISendableCommand, long, System.Threading.CancellationToken)"/> unchanged.
/// </para>
/// </remarks>
public interface ISendableCommand<TAnswer> : ISendableCommand;
