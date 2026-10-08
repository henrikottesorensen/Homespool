using System;
using System.Threading;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The caller stopped waiting after the printer already had the command, so the command may still
/// be acted on.
/// </summary>
/// <remarks>
/// <para>
/// <b>An <see cref="OperationCanceledException"/>, because to everything above the sender it is
/// one:</b> the caller asked to stop, and every filter that ends work on its own token still does.
/// A plain cancellation says the command never reached the printer; this one says it did, which only
/// a caller holding something the printer will come back for needs to tell apart. An offer is that
/// thing - revoking it here would answer the printer's fetch with a 404.
/// </para>
/// <para>
/// Not localised: a caller that has cancelled has nobody left to show a message to.
/// </para>
/// </remarks>
public class CommandCancelledAfterDeliveryException : OperationCanceledException
{
    /// <summary>The one callers actually use - the message is not worth restating at each throw.</summary>
    /// <param name="printerId">The printer that has the command.</param>
    /// <param name="commandId">The id it went out under, when the transport knows it.</param>
    /// <param name="cancellationToken">The caller's token, which is what was cancelled.</param>
    public CommandCancelledAfterDeliveryException(int printerId, uint? commandId, CancellationToken cancellationToken)
        : base($"The caller stopped waiting after printer {printerId} had been given the command.", cancellationToken)
    {
        CommandId = commandId;
    }

    // The three constructors every public exception type is expected to carry (CA1032). See
    // PrinterNotConnectedException for why they are here despite nothing calling them.
    public CommandCancelledAfterDeliveryException()
    {
    }

    public CommandCancelledAfterDeliveryException(string message)
        : base(message)
    {
    }

    public CommandCancelledAfterDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The id the command went out under, when known. A download the printer took is running anyway,
    /// and its end will quote this id back.
    /// </summary>
    public uint? CommandId { get; }
}
