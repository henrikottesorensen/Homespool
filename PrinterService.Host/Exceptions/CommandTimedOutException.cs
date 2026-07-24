using System;

namespace PrinterService.Host.Exceptions;

/// <summary>The printer never answered a sent command within the response timeout.</summary>
public class CommandTimedOutException : Exception
{
    public CommandTimedOutException(int printerId)
        : base($"Printer {printerId} did not respond to the command in time.")
    {
    }
}
