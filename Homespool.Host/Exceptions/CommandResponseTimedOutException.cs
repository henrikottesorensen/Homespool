using System;

namespace Homespool.Host.Exceptions;

/// <summary>The printer never answered a sent command within the response timeout.</summary>
/// <remarks>
/// The command did reach the socket, so this says nothing about whether the printer acted on it -
/// the firmware defers acks while warming up or homing. Contrast
/// <see cref="CommandSendTimedOutException"/>, where the write itself never completed and the
/// connection is torn down as a result.
/// </remarks>
public class CommandResponseTimedOutException : Exception
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public CommandResponseTimedOutException(int printerId)
        : base($"Printer {printerId} did not respond to the command in time.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). See
    // PrinterNotConnectedException for why they are here despite nothing calling them.
    public CommandResponseTimedOutException()
    {
    }

    public CommandResponseTimedOutException(string message)
        : base(message)
    {
    }

    public CommandResponseTimedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
