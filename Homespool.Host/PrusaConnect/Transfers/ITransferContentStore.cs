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
}
