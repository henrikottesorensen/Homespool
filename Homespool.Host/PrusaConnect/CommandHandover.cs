namespace Homespool.Host.PrusaConnect;

/// <summary>What became of a command handed to a connection.</summary>
/// <remarks>
/// <b>The distinction exists because the two transports deliver at different moments.</b> A socket
/// writes the frame and the printer has it; the pre-websocket HTTP transport can only park the
/// command until the printer's next telemetry POST collects it. <c>PrinterConnectionActor</c> starts
/// a command's response deadline on <see cref="Written"/> alone - a parked command timed against the
/// moment it was parked would report a healthy printer as unresponsive for the length of its own
/// poll interval.
/// </remarks>
public enum CommandHandover
{
    /// <summary>Never set. Reserved so a default-constructed value makes no claim.</summary>
    /// <remarks>
    /// <b>This slot was <see cref="Written"/> until 2026-08-22, and that was load-bearing by
    /// accident.</b> An uninitialised value asserted that a command had reached the printer, and
    /// started the response clock on it. Nothing in production reached that path - every assignment
    /// is definite - but an unconfigured test substitute did, so the actor's timeout tests were
    /// resting on which member happened to sit first rather than on a connection that said anything.
    /// </remarks>
    Undefined = 0,

    /// <summary>Written to the printer; the response clock may start.</summary>
    Written = 1,

    /// <summary>Held for the printer's next poll; the response clock waits for the hand-over.</summary>
    Parked = 2,
}
