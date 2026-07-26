using System;

namespace Homespool.FakePrinter;

/// <summary>
/// One reply a <see cref="CommandAnswerPolicy"/> schedules for a received command: an optional
/// payload to send after an optional delay, and optionally an abrupt disconnect afterwards (the
/// "printer died mid-command" case no real printer will produce on demand).
/// </summary>
/// <param name="Payload">The raw message to send, or null to send nothing (delay/disconnect only).</param>
/// <param name="Delay">Wait before sending; zero replies immediately, like a healthy printer.</param>
/// <param name="DisconnectAfter">Abort the socket - no close handshake - after this reply.</param>
public sealed record PlannedReply(byte[]? Payload, TimeSpan Delay = default, bool DisconnectAfter = false);
