namespace Homespool.Host.Firmware;

/// <summary>Which printers are having firmware installed, and with which images, right now.</summary>
/// <remarks>
/// What everything outside the install asks before it gives a printer work or takes an image away:
/// the queue, a direct send, deleting a stored image. An interface so each of those can be built in a
/// test without the install's own machinery.
/// </remarks>
public interface IFirmwareInstallations
{
    /// <summary>Whether firmware is being installed on <paramref name="printerId"/>, from the start until it is done or has failed.</summary>
    bool IsInstalling(int printerId);

    /// <summary>The stored image being installed on <paramref name="printerId"/>, or null when nothing is.</summary>
    long? ImageBeingInstalled(int printerId);

    /// <summary>Whether the stored image <paramref name="fileId"/> is being installed on any printer.</summary>
    bool IsInstallingImage(long fileId);
}
