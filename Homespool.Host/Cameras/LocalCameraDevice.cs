namespace Homespool.Host.Cameras;

/// <summary>
/// A video device attached to this machine.
/// </summary>
/// <param name="Name">
/// The udev by-id name, e.g. <c>usb-046d_0821_437242E0-video-index0</c>. This is what goes into a
/// source string, and what makes the choice survive a reboot.
/// </param>
/// <param name="Description">The same thing, tidied for a person to read.</param>
public sealed record LocalCameraDevice(string Name, string Description);
