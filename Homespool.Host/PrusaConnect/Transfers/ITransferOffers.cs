namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// The write side of the offer registry: what a request handler calls before commanding a printer to
/// download something. Separate from <see cref="ITransferContentStore"/> so the connection actor,
/// which only ever resolves a hash, cannot register or revoke one.
/// </summary>
public interface ITransferOffers
{
    /// <summary>
    /// Offers <paramref name="path"/> under <paramref name="hash"/>, which is what the printer will
    /// quote back on the first range request of the transfer.
    /// </summary>
    /// <remarks>
    /// The hash is supplied rather than generated here because it is already the uploaded file's
    /// identifier - one value serves as both the file handle and the transfer token, which is how
    /// Connect's own app API works (its <c>start/cloud</c> takes a <c>hash</c>, and that same hash
    /// comes back from the printer). Re-offering an already-offered hash is fine and is what a
    /// retried transfer does.
    /// </remarks>
    void Offer(string hash, string path);

    /// <summary>
    /// Withdraws an offer. Idempotent - an already-withdrawn or never-known hash is not an error,
    /// because the transfer ending and an operator cancelling can race.
    /// </summary>
    void Revoke(string hash);
}
