namespace Homespool.Host.Firmware;

/// <summary>Where a firmware flash has got to.</summary>
public enum FirmwareFlashStage
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>The image is being sent to the printer's drive.</summary>
    Sending = 1,

    /// <summary>The printer has taken the transfer; waiting for the image to arrive whole.</summary>
    Arriving = 2,

    /// <summary>The flash has been sent; the printer is restarting into its bootloader.</summary>
    Restarting = 3,

    /// <summary>The printer is back on the image's version; removing the image from its drive.</summary>
    CleaningUp = 4,

    /// <summary>The printer runs the image's version.</summary>
    Done = 5,

    /// <summary>The flash stopped; its status says where and why.</summary>
    Failed = 6,
}
