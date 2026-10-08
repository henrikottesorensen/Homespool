using System;

using Homespool.Host.Localisation;

namespace Homespool.Host.Firmware;

/// <summary>One printer's firmware flash, as far as it has got.</summary>
/// <param name="PrinterId">The printer being flashed.</param>
/// <param name="ImageId">The stored image's row.</param>
/// <param name="ImageName">The image's name, as it was uploaded.</param>
/// <param name="Version">The version the image carries, and the one the printer must come back on.</param>
/// <param name="Stage">Where the flash has got to.</param>
/// <param name="StartedAt">When it was started.</param>
/// <param name="Problem">Why it stopped, when <paramref name="Stage"/> is <see cref="FirmwareFlashStage.Failed"/>.</param>
public sealed record FirmwareFlashStatus(int PrinterId,
                                         long ImageId,
                                         string ImageName,
                                         string Version,
                                         FirmwareFlashStage Stage,
                                         DateTimeOffset StartedAt,
                                         MessageKey? Problem = null)
{
    /// <summary>Whether the flash is still under way - neither done nor failed.</summary>
    public bool IsRunning => Stage is not (FirmwareFlashStage.Done or FirmwareFlashStage.Failed);
}
