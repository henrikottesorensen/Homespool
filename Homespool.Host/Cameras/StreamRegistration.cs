namespace Homespool.Host.Cameras;

/// <summary>
/// What became of registering a camera with the stream server.
/// </summary>
/// <remarks>
/// <b>Four rather than a yes or no, because the cameras page says a different thing for each.</b>
/// "Check the address" is right when the sidecar refused the source and wrong when it could not write
/// its own configuration file - which it also answers with a 400 - and it sends somebody hunting
/// through a camera's settings for a fault that is in the sidecar.
/// </remarks>
public enum StreamRegistration
{
    /// <summary>Reserved so a default-constructed value is not a meaningful outcome.</summary>
    Undefined = 0,

    /// <summary>The sidecar took the stream and saved it.</summary>
    Registered = 1,

    /// <summary>The sidecar refused the source itself: a kind it does not serve, or will not run.</summary>
    SourceRefused = 2,

    /// <summary>
    /// The sidecar accepted the source but could not save it to its configuration file - a file it
    /// cannot write, or one it cannot parse. Nothing about the camera's address is wrong.
    /// </summary>
    ConfigurationNotSaved = 3,

    /// <summary>The sidecar could not be asked: no credential, unreachable, timed out, or it answered something else.</summary>
    Unavailable = 4,
}
