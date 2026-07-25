using System;

namespace PrinterService.Host.Exceptions;

/// <summary>The printer never answered a sent command within the response timeout.</summary>
public class CommandTimedOutException : Exception
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public CommandTimedOutException(int printerId)
        : base($"Printer {printerId} did not respond to the command in time.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). See
    // PrinterNotConnectedException for why they are here despite nothing calling them.
    public CommandTimedOutException()
    {
    }

    public CommandTimedOutException(string message)
        : base(message)
    {
    }

    public CommandTimedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
