namespace Homespool.Host.Printing;

/// <summary>What became of an older copy, and the printer's words when it kept it.</summary>
/// <param name="Removal">What happened.</param>
/// <param name="Reason">
/// The printer's refusal, as it sent it, when <paramref name="Removal"/> is
/// <see cref="OutdatedCopyRemoval.InUse"/> or <see cref="OutdatedCopyRemoval.Refused"/>; otherwise null.
/// Text from a printer, so it is encoded wherever it is rendered and cleaned wherever it is logged.
/// </param>
public sealed record OutdatedCopyOutcome(OutdatedCopyRemoval Removal, string? Reason = null)
{
    /// <summary>Whether a send may go ahead - nothing older is left under the name.</summary>
    public bool Cleared => Removal is OutdatedCopyRemoval.NothingToRemove or OutdatedCopyRemoval.Removed;
}
