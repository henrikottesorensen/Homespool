using System;

namespace Homespool.Host.Exceptions;

/// <summary>The printer never answered a sent command within the response timeout.</summary>
/// <remarks>
/// The command did reach the socket, so this says nothing about whether the printer acted on it -
/// the firmware defers acks while warming up or homing. Contrast
/// <see cref="CommandSendTimedOutException"/>, where the write itself never completed and the
/// connection is torn down as a result.
/// </remarks>
public class CommandResponseTimedOutException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    public CommandResponseTimedOutException(int printerId)
        : base($"Printer {printerId} did not respond to the command in time.")
    {
    }

    /// <summary>For a command known to have gone out under <paramref name="commandId"/>.</summary>
    /// <param name="printerId">The printer that did not answer.</param>
    /// <param name="commandId">The id it went out under, when the transport knows it.</param>
    public CommandResponseTimedOutException(int printerId, uint? commandId)
        : this(printerId)
    {
        CommandId = commandId;
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

    /// <summary>
    /// The id the unanswered command went out under, when known. A download the printer acknowledges
    /// late is often running anyway, and its end will quote this id back.
    /// </summary>
    public uint? CommandId { get; }

    /// <inheritdoc />
    public string ResourceKey => "Error_CommandNoAnswer";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
