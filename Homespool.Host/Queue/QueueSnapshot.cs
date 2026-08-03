using Homespool.Model;

namespace Homespool.Host.Queue;

/// <summary>Everything <see cref="QueueRules.Decide"/> looks at, gathered at one moment.</summary>
/// <remarks>
/// A snapshot rather than a "state", because it spans three subjects - the connection, the printer
/// and the queue - and only one of them is the queue's. Naming it for the smallest part it carries
/// would also put a second meaning of "state" next to <c>PrinterLiveState</c> and
/// <see cref="PrinterStatus"/>, which is the most overloaded word in this codebase already.
/// </remarks>
/// <param name="Connected">Whether the printer has a live WebSocket right now.</param>
/// <param name="Status">Its last-known state, from <c>PrinterLiveState</c>.</param>
/// <param name="Head">The queue's first entry, or null when the queue is empty.</param>
/// <param name="TransferInFlight">Whether this printer is already pulling a file from us.</param>
/// <param name="PrintInFlight">
/// Whether a print of ours is open on this printer - commanded and not yet ended.
/// <para>
/// <b>Load-bearing for about three seconds, which is exactly long enough to matter.</b> A printer
/// accepts <c>START_PRINT</c> and keeps reporting <c>READY</c> until it has finished preview-init
/// and heating - measured at 3.1 s in the Core One capture. Without this the loop would see a ready
/// printer with a queue and command a second print into that gap.
/// </para>
/// </param>
public sealed record QueueSnapshot(bool Connected,
                                   PrinterStatus Status,
                                   QueueHead? Head,
                                   bool TransferInFlight,
                                   bool PrintInFlight = false);
