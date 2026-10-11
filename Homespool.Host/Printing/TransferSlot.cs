namespace Homespool.Host.Printing;

/// <summary>
/// What a printer has said about its one transfer slot over the connection it holds now.
/// </summary>
/// <remarks>
/// <para>
/// <b>The printer's word, not our bookkeeping.</b> The slot is shared by every way a file reaches the
/// drive - our offers, a PrusaLink upload, a direct send - and only the printer sees all of them. A
/// row of ours can say a transfer of ours is running; only this says whether anything is.
/// </para>
/// <para>
/// <b>Of the current connection only</b>, so nothing from an earlier one stands in for it: a printer
/// that has not reported since connecting is <see cref="Unreported"/>, which is not
/// <see cref="Free"/>.
/// </para>
/// </remarks>
public enum TransferSlot
{
    Undefined = 0,

    /// <summary>Connected, and nothing reported yet - or not connected at all.</summary>
    Unreported = 1,

    /// <summary>The printer's last report named no transfer.</summary>
    Free = 2,

    /// <summary>The printer's last report named a transfer, whoever started it.</summary>
    Taken = 3,
}
