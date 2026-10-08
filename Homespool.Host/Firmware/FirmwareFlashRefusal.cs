namespace Homespool.Host.Firmware;

/// <summary>Why a firmware flash was not started.</summary>
public enum FirmwareFlashRefusal
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>No stored image fits this printer under that digest, or it no longer verifies.</summary>
    NoSuchImage = 1,

    /// <summary>The printer is not connected, so nothing can be sent to it.</summary>
    NotConnected = 2,

    /// <summary>The printer is printing, busy, or has not said what it is doing since it connected.</summary>
    Busy = 3,

    /// <summary>The printer has work queued, which the queue could start between the image and the flash.</summary>
    Queued = 4,

    /// <summary>A flash of this printer is already under way.</summary>
    AlreadyFlashing = 5,
}
