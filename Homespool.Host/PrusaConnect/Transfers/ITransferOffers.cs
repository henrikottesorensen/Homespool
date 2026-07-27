namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// The write side of the offer registry: what a request handler calls before commanding a printer to
/// download something. Separate from <see cref="ITransferContentStore"/> so the connection actor,
/// which only ever resolves a hash, cannot register or revoke one.
/// </summary>
public interface ITransferOffers
{
    /// <summary>
    /// Offers <paramref name="path"/> under a freshly generated hash, and returns that hash to put in
    /// the <c>START_CONNECT_DOWNLOAD</c> command.
    /// </summary>
    /// <remarks>
    /// The hash is generated here rather than supplied, because its two constraints both belong to
    /// this layer: it must be unguessable (it is the only thing authorizing a download of this file)
    /// and it must fit firmware's 28-character buffer.
    /// </remarks>
    string Offer(string path);

    /// <summary>
    /// Withdraws an offer. Idempotent - an already-withdrawn or never-known hash is not an error,
    /// because the transfer ending and an operator cancelling can race.
    /// </summary>
    void Revoke(string hash);
}
