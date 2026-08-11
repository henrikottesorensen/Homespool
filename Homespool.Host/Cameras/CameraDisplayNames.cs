using System;

using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// What a camera is called on screen. One answer, so every page and endpoint gives the same one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved, never stored.</b> <c>Camera.Name</c> holds only what a person typed, and stays null
/// until they type something — the rule <c>Camera.Name</c> and <c>Printer.Name</c> both document,
/// for the reason they both give: a default written into the column makes "the user chose this"
/// indistinguishable from "we defaulted it", after which the default can never be safely refreshed.
/// Everything below is worked out at display time, so clearing a name goes back to describing the
/// hardware and replugging a camera into another machine follows it.
/// </para>
/// <para>
/// <b>Why this is a service and not a fallback written at each call site.</b> The chain has three
/// links and a fourth that varies, which is enough for two hand-written copies to disagree — and
/// they did: the cameras list fell back to a uuid while a printer's page fell back to "Camera 2".
/// A caption that changes depending on which page you are looking at is a bug that no test would
/// call one, so the chain lives here and callers ask for the answer.
/// </para>
/// </remarks>
public sealed class CameraDisplayNames
{
    private readonly LocalCameraDevices _devices;

    public CameraDisplayNames(LocalCameraDevices devices)
    {
        _devices = devices;
    }

    /// <summary>
    /// The name to show for a camera.
    /// </summary>
    /// <param name="camera">The camera being described.</param>
    /// <param name="lastResort">
    /// What to say when nothing else can be worked out — for a network camera nobody has named,
    /// which reports nothing about itself. Callers with something better than a uuid to offer pass
    /// it here (a picture's position on a page, say); the uuid is used when they do not. It is only
    /// ever a last resort, so passing one cannot override a name somebody chose or hide the
    /// hardware's own.
    /// </param>
    public string For(Camera camera, string? lastResort = null)
    {
        ArgumentNullException.ThrowIfNull(camera);

        return camera.Name
            ?? _devices.DescribeSource(camera.Source)
            ?? lastResort
            ?? camera.Uuid.ToString();
    }
}
