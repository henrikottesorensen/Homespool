using Homespool.Host.Localisation;

namespace Homespool.Host.Cameras;

/// <summary>
/// What became of bringing one camera's stream into line with its row.
/// </summary>
/// <param name="Outcome">What the sidecar holds for the camera now.</param>
/// <param name="Refusal">
/// The rule's own words when the outcome is <see cref="StreamSyncOutcome.Withheld"/>, so a page can say
/// what is wrong with the source rather than that something is; otherwise <see langword="null"/>.
/// </param>
public sealed record StreamSync(StreamSyncOutcome Outcome, MessageKey? Refusal = null);
