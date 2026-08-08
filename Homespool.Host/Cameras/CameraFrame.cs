using System;

namespace Homespool.Host.Cameras;

/// <summary>
/// One image from a camera, and when it was taken.
/// </summary>
/// <param name="Bytes">The encoded image, exactly as the camera served it.</param>
/// <param name="ContentType">The media type the camera reported, e.g. <c>image/jpeg</c>.</param>
/// <param name="CapturedAt">
/// When the fetch completed. This is the closest honest answer available: the camera does not tell
/// us when it exposed the frame, and with a live source the difference is one frame interval. It is
/// deliberately not the time the frame is served, which would make an old image look new every time
/// it was handed out.
/// </param>
/// <remarks>
/// Held in memory only, and never written anywhere. The bytes are passed through untouched — the
/// cameras this is built against serve MJPEG, so their frames are already JPEG and re-encoding
/// would cost CPU to lose quality.
/// </remarks>
public sealed record CameraFrame(byte[] Bytes, string ContentType, DateTimeOffset CapturedAt)
{
    /// <summary>
    /// How old this frame is, given the current time.
    /// </summary>
    public TimeSpan AgeAt(DateTimeOffset now)
    {
        return now - CapturedAt;
    }
}
