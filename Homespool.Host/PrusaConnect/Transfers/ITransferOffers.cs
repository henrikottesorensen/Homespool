namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// The write side of the offer registry: what a request handler calls before commanding a printer to
/// download something. Separate from <see cref="ITransferContentStore"/> so the connection actor,
/// which only ever resolves a hash, cannot register or revoke one.
/// </summary>
public interface ITransferOffers
{
    /// <summary>
    /// Offers the file at <paramref name="path"/> under <paramref name="token"/>, which is what the
    /// printer will quote back on the first range request of the transfer. Returns false if the file
    /// could not be opened, which a caller that just looked it up should treat as it vanishing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The file is opened here, not when the printer asks.</b> That pins the bytes for the whole
    /// transfer: an overwrite replaces the name, and this offer keeps serving what the command
    /// declared. See <see cref="TransferOfferStore"/> for why the lazy version was a silent
    /// corruption rather than a lesser guarantee.
    /// </para>
    /// <para>
    /// The token is supplied rather than generated here because the caller has to put it in the
    /// command it is about to send. It is minted per send and means nothing afterwards - it is
    /// correlation, not identity, which is what lets the file it stands for be named anything at all.
    /// </para>
    /// </remarks>
    bool Offer(string token, string path);

    /// <summary>
    /// Withdraws an offer and closes what it held. Idempotent - an already-withdrawn or never-known
    /// token is not an error, because the transfer ending and an operator cancelling can race.
    /// </summary>
    void Revoke(string token);
}
