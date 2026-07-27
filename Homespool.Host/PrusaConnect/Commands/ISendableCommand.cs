using System.Collections.Generic;

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
    /// <see cref="CommandSendOutcome.Dispatched"/> the moment its frame is written, and never takes
    /// the one in-flight slot - because nothing will ever free it. The alternative is worse than it
    /// sounds: the caller would wait out the full response timeout and be told the command failed,
    /// for a command that did exactly what was asked.
    /// </para>
    /// <para>
    /// <b>Not a guess about slow printers.</b> This is only for commands the firmware
    /// <i>structurally</i> cannot acknowledge - where answering would require code to run after the
    /// point of no return. A merely slow answer is <see cref="CommandSendOutcome.ResponseTimedOut"/>
    /// and stays that way; do not reach for this to paper over one.
    /// </para>
    /// </remarks>
    bool ExpectsReply => true;
}
