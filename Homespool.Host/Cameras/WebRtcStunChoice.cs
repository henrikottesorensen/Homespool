namespace Homespool.Host.Cameras;

/// <summary>
/// Whether the stream server may ask a public STUN server for this deployment's address, and which.
/// </summary>
/// <remarks>
/// <b>The server is part of the choice only while STUN is on</b>, so two choices that both turn it
/// off are equal whatever server each names - an edit to the address of a server nobody contacts is
/// not a change worth restarting the stream server for.
/// </remarks>
/// <param name="Enabled">Whether STUN is allowed.</param>
/// <param name="Server">The STUN server to ask, or empty when STUN is off.</param>
public sealed record WebRtcStunChoice(bool Enabled, string Server)
{
    /// <summary>The choice the options describe.</summary>
    /// <param name="options">The camera options.</param>
    /// <returns>The choice, with the server cleared when STUN is off.</returns>
    public static WebRtcStunChoice From(CameraOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(options);

        return new WebRtcStunChoice(options.WebRtcStunEnabled,
                                    options.WebRtcStunEnabled ? options.WebRtcStunServer.Trim() : string.Empty);
    }
}
