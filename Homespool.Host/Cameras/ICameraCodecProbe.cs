using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Cameras;

/// <summary>
/// Asks the stream server which video codecs a camera produces.
/// </summary>
/// <remarks>
/// An interface over one method of <see cref="Go2RtcClient"/>, so that
/// <see cref="CameraLiveAvailability"/> can be tested without a stream server. The client stays the
/// single chokepoint for reaching the sidecar; this only names the one question the availability
/// logic asks of it.
/// </remarks>
public interface ICameraCodecProbe
{
    /// <summary>
    /// The video codecs this camera's stream carries, or <see langword="null"/> when the camera did
    /// not answer - which a caller must treat as "not right now" rather than "never".
    /// </summary>
    Task<IReadOnlySet<string>?> ProbeCodecsAsync(Guid streamName, CancellationToken cancellationToken);
}
