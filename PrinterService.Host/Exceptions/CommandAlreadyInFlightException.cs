using System;

namespace PrinterService.Host.Exceptions;

/// <summary>
/// A command is already pending a reply for this printer. The firmware itself only holds one
/// command buffer at a time (Prusa-Firmware-Buddy connect.cpp:469-476 at the pinned ref); this
/// mirrors that limit on our side so a second click fails fast rather than racing it.
/// </summary>
public class CommandAlreadyInFlightException : Exception
{
    public CommandAlreadyInFlightException(int printerId)
        : base($"Printer {printerId} is still processing a previous command.")
    {
    }
}
