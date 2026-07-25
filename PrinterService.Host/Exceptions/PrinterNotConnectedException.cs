using System;

namespace PrinterService.Host.Exceptions;

/// <summary>The printer has no live WebSocket connection right now, so a command can't be sent.</summary>
public class PrinterNotConnectedException : Exception
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public PrinterNotConnectedException(int printerId)
        : base($"Printer {printerId} is not currently connected.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). Nothing in
    // this project calls them, but leaving them off makes the type awkward for anything that wants
    // to wrap or rethrow it with context of its own.
    public PrinterNotConnectedException()
    {
    }

    public PrinterNotConnectedException(string message)
        : base(message)
    {
    }

    public PrinterNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
