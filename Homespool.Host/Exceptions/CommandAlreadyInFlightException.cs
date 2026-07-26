using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A command is already pending a reply for this printer. The firmware itself only holds one
/// command buffer at a time (Prusa-Firmware-Buddy connect.cpp:469-476 at the pinned ref); this
/// mirrors that limit on our side so a second click fails fast rather than racing it.
/// </summary>
public class CommandAlreadyInFlightException : Exception
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public CommandAlreadyInFlightException(int printerId)
        : base($"Printer {printerId} is still processing a previous command.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). See
    // PrinterNotConnectedException for why they are here despite nothing calling them.
    public CommandAlreadyInFlightException()
    {
    }

    public CommandAlreadyInFlightException(string message)
        : base(message)
    {
    }

    public CommandAlreadyInFlightException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
