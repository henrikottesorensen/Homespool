using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Cameras;

/// <summary>
/// Fetches one image from a camera's address.
/// </summary>
public interface ICameraSnapshotFetcher
{
    /// <summary>
    /// Fetches a single frame, or returns <see langword="null"/> if the camera could not be read.
    /// </summary>
    /// <remarks>
    /// Never throws for an unreachable or misbehaving camera — a camera being off is ordinary, not
    /// exceptional, and the caller's job is to show that rather than to handle an exception.
    /// </remarks>
    Task<CameraFrame?> FetchAsync(string address, CancellationToken cancellationToken);
}
