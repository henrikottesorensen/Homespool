namespace Homespool.Host.Http;

/// <summary>
/// Header names that are neither in <c>HeaderNames</c> nor part of the Prusa Connect printer protocol.
/// </summary>
public static class CustomHeaderNames
{
    /// <summary>
    /// The client's address as the shipped nginx sets it (<c>proxy_set_header X-Real-IP $remote_addr</c>).
    /// </summary>
    public const string RealIp = "X-Real-IP";

    /// <summary>
    /// Tells nginx not to buffer the response, so a live camera stream reaches the browser frame by frame.
    /// </summary>
    public const string AccelBuffering = "X-Accel-Buffering";

    /// <summary>When a camera frame was captured, on the snapshot response.</summary>
    public const string FrameCapturedAt = "X-Frame-Captured-At";

    /// <summary>
    /// Carries a personal access token with no scheme in front of it, as OctoPrint clients send it.
    /// </summary>
    public const string ApiKey = "X-Api-Key";
}
