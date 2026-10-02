namespace Homespool.Host.Cameras;

/// <summary>
/// What a camera's stream became when the sidecar was brought into line with the camera's row.
/// </summary>
/// <remarks>
/// <b>Registered and unchanged are two answers, not one.</b> Both leave the sidecar holding the
/// camera's source, but only the first replaced a stream - which a viewer already watching does not
/// follow - so a caller counting what it put back must not count what it left alone.
/// </remarks>
public enum StreamSyncOutcome
{
    /// <summary>Reserved so a default-constructed value is not a meaningful outcome.</summary>
    Undefined = 0,

    /// <summary>The sidecar was handed the camera's source, and took it and saved it.</summary>
    Registered = 1,

    /// <summary>The sidecar already held exactly the camera's source, and was left alone.</summary>
    Unchanged = 2,

    /// <summary>There is no such camera, and the sidecar confirmed it holds no stream for it.</summary>
    Removed = 3,

    /// <summary>
    /// The camera's source fails a rule about what may be handed to the sidecar, so it was not, and any
    /// stream the sidecar held for the camera was asked to go.
    /// </summary>
    Withheld = 4,

    /// <summary>The sidecar refused the source itself: a kind it does not serve, or will not run.</summary>
    SourceRefused = 5,

    /// <summary>The sidecar took the source but could not save it to its configuration file.</summary>
    ConfigurationNotSaved = 6,

    /// <summary>The sidecar could not be asked, or did not confirm a removal.</summary>
    Unavailable = 7,
}
