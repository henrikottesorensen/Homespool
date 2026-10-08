using System;

namespace Homespool.Host.Firmware;

/// <summary>How long a firmware flash waits at each step, and how often it looks.</summary>
/// <param name="PollInterval">How often progress is read back from the database while waiting.</param>
/// <param name="ArrivalTimeout">
/// How long an image may take to arrive. A few megabytes over a printer's Wi-Fi is a minute or two;
/// this is the point at which it is not coming.
/// </param>
/// <param name="RestartTimeout">
/// How long a printer may take to flash and reconnect on the image's version. Prusa's bootloader
/// writes the firmware and its resources, which takes a few minutes; this is the point at which it is
/// not coming back on it.
/// </param>
/// <param name="PathGrace">
/// How long an arrived image may go without its short path being reported before the printer is asked
/// for it. The report usually comes with the arrival.
/// </param>
/// <remarks>
/// A service of its own rather than constants so a test can wait seconds where a printer needs
/// minutes; nothing configures it otherwise.
/// </remarks>
public sealed record FirmwareFlashTimings(TimeSpan PollInterval,
                                          TimeSpan ArrivalTimeout,
                                          TimeSpan RestartTimeout,
                                          TimeSpan PathGrace)
{
    /// <summary>What a real printer needs.</summary>
    public static FirmwareFlashTimings Default { get; } = new(PollInterval: TimeSpan.FromSeconds(2),
                                                              ArrivalTimeout: TimeSpan.FromMinutes(10),
                                                              RestartTimeout: TimeSpan.FromMinutes(10),
                                                              PathGrace: TimeSpan.FromSeconds(10));
}
