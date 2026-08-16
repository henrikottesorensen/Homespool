using Homespool.Host.Localisation;

namespace Homespool.Host.Cameras;

/// <summary>
/// Whether a camera source may be handed to the stream server, and why not when it may not.
/// </summary>
/// <param name="IsAcceptable">Whether the source passed every check.</param>
/// <param name="Error">
/// Which sentence to show the person who typed it, or <see langword="null"/>. Written to say what
/// to do: the refusals here are for addresses that look perfectly reasonable, so "invalid" would
/// send someone hunting a typo that is not there.
/// </param>
/// <remarks>
/// <b>A key rather than a sentence</b>, because this is decided by a policy class with no request
/// and no culture, and rendered by a page that has both. See <see cref="MessageKey"/>.
/// </remarks>
public sealed record CameraSourceCheck(bool IsAcceptable, MessageKey? Error)
{
    /// <summary>A source that passed.</summary>
    public static CameraSourceCheck Accepted { get; } = new(true, null);

    /// <summary>A source that did not, with the reason.</summary>
    public static CameraSourceCheck Refused(string key, params object[] arguments)
    {
        return new CameraSourceCheck(false, MessageKey.For(key, arguments));
    }
}
