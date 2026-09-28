using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Telemetry;

/// <summary>
/// The part of a printer's live state an <see cref="ILiveStateObserver"/> is shown: values, copied,
/// so nothing an observer does can reach the writer's cache.
/// </summary>
/// <param name="Status">What the printer says it is doing.</param>
/// <param name="AttentionCode">The code of the dialog it is showing, if any.</param>
/// <param name="AttentionText">The text of that dialog, when the printer sent words.</param>
/// <param name="JobId">The printer's own job number, while it has one.</param>
public readonly record struct LiveStateSnapshot(PrinterStatus Status,
                                                int? AttentionCode,
                                                string? AttentionText,
                                                int? JobId)
{
    /// <summary>Copies the observed fields out of <paramref name="state"/>.</summary>
    public static LiveStateSnapshot Of(PrinterLiveState state)
    {
        System.ArgumentNullException.ThrowIfNull(state);

        return new LiveStateSnapshot(state.Status, state.AttentionCode, state.AttentionText, state.JobId);
    }
}
