using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// What happened when a camera was saved: refused, saved and working, or saved and silent.
/// </summary>
/// <param name="Camera">The saved camera, or <see langword="null"/> if nothing was saved.</param>
/// <param name="Error">Why nothing was saved, or <see langword="null"/>.</param>
/// <param name="Warning">
/// Saved, but something is not right yet - the stream server would not take it, or the camera did
/// not produce a picture.
/// </param>
/// <remarks>
/// <b>Three states rather than two, because "saved" and "working" are different facts.</b> A camera
/// that is switched off, or whose address has a typo in the path rather than the host, saves
/// perfectly well and produces nothing. Refusing to save it would be wrong - it may be unplugged
/// while it is being set up - and reporting success would be a lie. So it saves and says so.
/// </remarks>
public sealed record CameraSaveOutcome(Camera? Camera, MessageKey? Error, MessageKey? Warning)
{
    /// <summary>Whether anything was written.</summary>
    public bool Saved => Camera is not null;

    /// <summary>Nothing was saved, and this is why.</summary>
    public static CameraSaveOutcome Refused(string key, params object[] arguments)
    {
        return new CameraSaveOutcome(null, MessageKey.For(key, arguments), null);
    }

    /// <summary>Nothing was saved, and a policy check already decided the wording.</summary>
    public static CameraSaveOutcome Refused(MessageKey error)
    {
        return new CameraSaveOutcome(null, error, null);
    }

    /// <summary>Saved, and a picture came back.</summary>
    public static CameraSaveOutcome Working(Camera camera)
    {
        return new CameraSaveOutcome(camera, null, null);
    }

    /// <summary>Saved, but no picture yet.</summary>
    public static CameraSaveOutcome Silent(Camera camera, string key, params object[] arguments)
    {
        return new CameraSaveOutcome(camera, null, MessageKey.For(key, arguments));
    }
}
