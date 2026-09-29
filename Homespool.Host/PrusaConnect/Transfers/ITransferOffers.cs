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
    /// <para>
    /// <b><paramref name="printerId"/> is who the offer is for, and the only credential that can
    /// open it.</b> The token alone used to be enough, on the reasoning that it is unguessable - but
    /// the encrypted path deliberately puts it on the wire in the clear as the IV, so "unguessable"
    /// and "secret" had quietly parted ways: any enrolled printer that saw an IV could open the
    /// offer as plaintext through the SDK's raw route. Binding the offer to the printer the command
    /// went to closes that without changing what any printer sends.
    /// </para>
    /// </remarks>
    bool Offer(string token, string path, int printerId);

    /// <summary>
    /// Withdraws an offer and closes what it held. Idempotent - an already-withdrawn or never-known
    /// token is not an error, because the transfer ending and an operator cancelling can race.
    /// </summary>
    void Revoke(string token);

    /// <summary>
    /// Whether an offer of a file named <paramref name="fileName"/> still stands for
    /// <paramref name="printerId"/> - one the printer is pulling, or has been told to fetch and has not
    /// yet opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one observation of a transfer that needs nothing from the printer.</b> A printer can only
    /// fetch bytes through a standing offer, so where none stands no transfer of that file can be
    /// running, whatever the command that started it was or was not told. Offers leave on every path
    /// that ends one: a refusal or a failed send revokes it, a terminal transfer event releases it, an
    /// offer never collected is swept, and a restart loses them all.
    /// </para>
    /// <para>
    /// <b>One the printer collected still answers true for a short while after it ends</b>, until the
    /// printer's own report of the end has had time to reach the event log - see
    /// <see cref="TransferOfferStore.EndReportedWithin"/>. Without that, "no offer" in the gap would
    /// read as a command never taken.
    /// </para>
    /// <para>
    /// By name rather than by token, because the queue that asks does not keep tokens: they are
    /// minted per send and mean nothing afterwards. Two users' files of one name to one printer are
    /// indistinguishable here, which costs only a wait - the printer has one transfer slot either way.
    /// </para>
    /// </remarks>
    bool IsOffered(int printerId, string fileName);
}
