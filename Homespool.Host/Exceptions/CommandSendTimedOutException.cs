using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The command could not be written to the printer's socket within the send deadline, so whether it
/// arrived is unknown.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="PrinterNotConnectedException"/>: that asserts the command never left,
/// which this case cannot claim. The peer stopped draining its socket, so any prefix of the frame
/// may already be in its buffer. Nor is it <see cref="CommandResponseTimedOutException"/>, which means the
/// frame demonstrably arrived and no answer came back. Callers that retry should treat this as "may
/// have happened" - for an idempotent command like Pause that is harmless, and for anything else it
/// is a genuine ambiguity rather than a shortcoming of the message.
/// </remarks>
public class CommandSendTimedOutException : Exception
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public CommandSendTimedOutException(int printerId)
        : base($"Writing the command to printer {printerId} did not complete in time; "
               + "whether the printer received it is unknown. The connection has been torn down.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). Nothing in
    // this project calls them, but leaving them off makes the type awkward for anything that wants
    // to wrap or rethrow it with context of its own.
    public CommandSendTimedOutException()
    {
    }

    public CommandSendTimedOutException(string message)
        : base(message)
    {
    }

    public CommandSendTimedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
