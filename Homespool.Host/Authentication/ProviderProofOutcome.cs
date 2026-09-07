namespace Homespool.Host.Authentication;

/// <summary>What came back from sending a password-less account to its provider.</summary>
/// <param name="Refusal">
/// Why the round trip earned no proof - <c>"failed"</c>, <c>"mismatch"</c> or <c>"stale"</c> - or
/// <see langword="null"/> when a proof is now waiting to be spent.
/// </param>
/// <param name="Provider">
/// What to call the provider in the sentence the page shows, or <see langword="null"/> when the round
/// trip did not get far enough to name one.
/// </param>
public readonly record struct ProviderProofOutcome(string? Refusal, string? Provider);
