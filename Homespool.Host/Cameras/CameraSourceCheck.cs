namespace Homespool.Host.Cameras;

/// <summary>
/// Whether a camera source may be handed to the stream server, and why not when it may not.
/// </summary>
/// <param name="IsAcceptable">Whether the source passed every check.</param>
/// <param name="Error">
/// A sentence for the person who typed it, or <see langword="null"/>. Written to say what to do:
/// the refusals here are for addresses that look perfectly reasonable, so "invalid" would send
/// someone hunting a typo that is not there.
/// </param>
public sealed record CameraSourceCheck(bool IsAcceptable, string? Error)
{
    /// <summary>A source that passed.</summary>
    public static CameraSourceCheck Accepted { get; } = new(true, null);

    /// <summary>A source that did not, with the reason.</summary>
    public static CameraSourceCheck Refused(string error)
    {
        return new CameraSourceCheck(false, error);
    }
}
