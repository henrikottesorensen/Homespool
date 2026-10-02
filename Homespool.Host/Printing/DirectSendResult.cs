namespace Homespool.Host.Printing;

/// <summary>What a direct send did: the older copy it had to clear first, and the send itself.</summary>
/// <param name="Cleared">What became of an older version under the file's name on that drive.</param>
/// <param name="Sent">
/// The send, or null when it was not attempted because <paramref name="Cleared"/> says the older copy
/// is still there.
/// </param>
public sealed record DirectSendResult(OutdatedCopyOutcome Cleared, FileSendResult? Sent);
