using Homespool.Host.Printing;

namespace Homespool.Host.Services;

/// <summary>
/// What a file send did: the command that actually went on the wire, and how the printer answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire name is here because the caller cannot know it.</b>
/// <see cref="PrintFileSender.SendAsync"/> chooses between two commands from a property of the
/// printer's connection - an inline transfer over its socket, or an encrypted download it fetches
/// itself - and that choice is deliberately invisible to whoever asked for the file to be sent.
/// A caller that then names a command in a refusal has to be told which one, or it will guess.
/// </para>
/// <para>
/// <b>It guessed, and was wrong for a year of Tuesdays.</b> <c>PrinterController.SendFile</c> held
/// <c>const string wireName = "START_CONNECT_DOWNLOAD"</c> and reported it on every failure - so a
/// printer on the pre-websocket transport, which is sent <c>START_ENCRYPTED_DOWNLOAD</c>, produced a
/// refusal naming a command it had never been sent. Found 2026-08-18 while driving the Python SDK,
/// where the refusal was the only evidence available and pointed at the wrong half of the code.
/// </para>
/// </remarks>
/// <param name="WireName">The command that was sent, as it appears on the wire.</param>
/// <param name="Outcome">
/// How the printer answered, or <see langword="null"/> where the send completed without one.
/// </param>
public sealed record FileSendResult(string WireName, CommandOutcome? Outcome);
