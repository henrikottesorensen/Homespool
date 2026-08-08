using System;

namespace Homespool.Host.Cameras;

/// <summary>
/// The result of checking a camera address: the parsed address, or why it was refused.
/// </summary>
/// <param name="Uri">The parsed address when acceptable, otherwise <see langword="null"/>.</param>
/// <param name="Error">
/// A sentence for the person who typed it, otherwise <see langword="null"/>. Written to say what to
/// do rather than what is wrong — an <c>rtsp://</c> address is a reasonable thing to try, and
/// "invalid URL" would send someone hunting for a typo that is not there.
/// </param>
/// <remarks>
/// A result type rather than two <c>out</c> parameters, which CA1021 refuses and which read poorly
/// at the call site regardless.
/// </remarks>
public sealed record CameraAddressCheck(Uri? Uri, string? Error)
{
    /// <summary>Whether the address may be used.</summary>
    public bool IsAcceptable => Uri is not null;
}
