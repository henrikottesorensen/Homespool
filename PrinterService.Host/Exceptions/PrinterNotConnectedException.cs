using System;

namespace PrinterService.Host.Exceptions;

/// <summary>The printer has no live WebSocket connection right now, so a command can't be sent.</summary>
public class PrinterNotConnectedException : Exception
{
    public PrinterNotConnectedException(int printerId)
        : base($"Printer {printerId} is not currently connected.")
    {
    }
}
