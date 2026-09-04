using System.Diagnostics.CodeAnalysis;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Resolves the hash we put in a <c>START_CONNECT_DOWNLOAD</c> back to the bytes it stands for, when
/// the printer's first range request quotes it back at us.
/// </summary>
/// <remarks>
/// The hash is the only thing tying a request to an offer: firmware echoes it once, on the first
/// request of a transfer (render.cpp:104-108), and never again. So this lookup happens exactly once
/// per transfer, and every later request is matched on the printer's <c>file_id</c> instead.
/// </remarks>
public interface ITransferContentStore
{
    /// <summary>
    /// Opens the content offered under <paramref name="hash"/> to <paramref name="printerId"/>, or
    /// returns false if nothing is offered under it - an unknown hash is an ordinary occurrence (a
    /// stale retry after a restart), not an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An offer made for another printer is "nothing offered" too, and deliberately not a distinct
    /// answer: the caller learns that these bytes are not for it, and nothing about whether they
    /// exist for somebody else. See <see cref="ITransferOffers.Offer"/> for why the binding exists.
    /// </para>
    /// <para>The caller owns the returned <see cref="ITransferContent"/> and disposes it when the
    /// transfer ends.</para>
    /// </remarks>
    bool TryOpen(string hash, int printerId, [NotNullWhen(true)] out ITransferContent? content);

    /// <summary>
    /// Retires what <paramref name="printerId"/> was offered, because the printer has reported the
    /// transfer over: the offer under <paramref name="hash"/> when the caller knows it, or - when it
    /// does not - every offer made to that printer that nothing is currently reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the read side deliberately.</b> This is not <see cref="ITransferOffers.Revoke"/>: a
    /// connection actor may only give back what its own printer was given, never withdraw an offer
    /// made to another, and the printer id is what enforces that. An offer under the hash that is
    /// bound to a different printer is left exactly as it was.
    /// </para>
    /// <para>
    /// <b>The hash is null on the paths where the actor never learns it.</b> An encrypted download
    /// and the SDK's raw fetch are separate HTTP requests, so the actor sees the printer's
    /// <c>TRANSFER_FINISHED</c> without ever having seen the offer token. Firmware runs one transfer
    /// at a time, so "everything idle for this printer" is that one transfer plus whatever a
    /// timed-out send left standing - and idle is what keeps a fetch in flight from being cut.
    /// </para>
    /// </remarks>
    void Release(int printerId, string? hash);
}
