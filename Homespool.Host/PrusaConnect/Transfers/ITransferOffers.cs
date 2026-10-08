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
    /// printer will quote back on the first range request of the transfer. Returns the length of the
    /// bytes pinned, or null if the file could not be opened, which a caller that just looked it up
    /// should treat as it vanishing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The file is opened here, not when the printer asks.</b> That pins the bytes for the whole
    /// transfer: an overwrite replaces the name, and this offer keeps serving what the command
    /// declared. See <see cref="TransferOfferStore"/> for why the lazy version was a silent
    /// corruption rather than a lesser guarantee.
    /// </para>
    /// <para>
    /// <b>The length is returned because the command has to declare it.</b> The printer fetches
    /// exactly the size it is told, so a size taken from an earlier look at the file - before an
    /// overwrite replaced it - would cut the newer bytes short or send the printer past their end.
    /// Read from the handle, it is the size of what this offer serves, whatever the name holds now.
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
    long? Offer(string token, string path, int printerId);

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

    /// <summary>
    /// Whether any offer still stands for <paramref name="printerId"/>: a transfer the printer may be
    /// pulling, or has been told to fetch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a firmware install asks before it flashes: the printer restarts under any transfer still
    /// running, whoever started it. Its own image's offer is gone by the time it flashes - released as
    /// the transfer's end arrives, before that end is saved and read as the image having arrived.
    /// </para>
    /// <para>
    /// <b>Standing offers only, unlike <see cref="IsOffered"/></b>: one released by its transfer's end
    /// is a transfer that has ended. The minute <see cref="IsOffered"/> adds covers the event log
    /// catching up, which the queue reads and an install does not.
    /// </para>
    /// </remarks>
    bool HasStandingOffer(int printerId);
}
