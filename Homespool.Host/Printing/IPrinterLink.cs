using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Printing;

/// <summary>
/// The protocol-neutral face of one printer's live connection - what the registry holds and what
/// <see cref="PrinterCommandService"/> sends through. An intent goes in; the link translates it
/// into its own protocol's command, because the connection is the one thing that knows which
/// protocol it speaks. Nothing above this interface does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately narrower than the protocol's own actor interface.</b> Prusa Connect's
/// <c>IPrinterConnectionActor</c> owns a socket, a mailbox, transfer state and command-id
/// allocation - all legitimately wire-shaped, per <c>notes/concurrency-model.md</c>. None of that
/// is a consumer's business, and a survey of every caller outside the Prusa edge found they ask
/// exactly two things of a connection: whether it is up, and to send an intent. That is this
/// interface, and it is why a second protocol's link is one class implementing two members.
/// </para>
/// <para>
/// Queries whose answers are wire payloads (a storage listing, an identity report) are outside
/// this interface on purpose - their answer shapes are per-protocol, and forcing a neutral answer
/// type before a second protocol has one would be inventing a vocabulary. They reach the Prusa
/// actor by an explicit downcast in <see cref="PrinterCommandService"/>, which is the honest
/// marker of that remaining work.
/// </para>
/// </remarks>
public interface IPrinterLink
{
    /// <summary>Whether the connection is up. Liveness for the UI, not a send guarantee.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Sends an intent in this link's own protocol and awaits the printer's correlated answer.
    /// <paramref name="cancellationToken"/> is the caller's own and propagates as an ordinary
    /// <see cref="System.OperationCanceledException"/>; disconnect and timeout are the link's business
    /// and come back as <see cref="CommandSendOutcome"/> values instead.
    /// </summary>
    Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken);

    /// <summary>
    /// Ends this link's life from the outside - what the registry does to a connection a newer one
    /// has displaced (last-wins, <see cref="PrinterConnectionRegistry"/>). Whatever the protocol,
    /// the link stops accepting work, fails anything in flight as
    /// <see cref="CommandSendOutcome.NotConnected"/>, and lets its own teardown follow.
    /// </summary>
    void Complete();
}
