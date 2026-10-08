using System;

namespace Homespool.Host.Firmware;

/// <summary>A stored firmware image, as a page lists it.</summary>
/// <param name="Digest">
/// The base64url SHA-256 its signature covers, which is also how a page names it: an image is what
/// Prusa signed, and the same firmware uploaded twice is one image.
/// </param>
/// <param name="Name">The name it was uploaded under, and the name it takes on a printer's drive.</param>
/// <param name="Header">What the image says it is.</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="UploadedAt">When it was stored.</param>
public sealed record FirmwareImage(string Digest,
                                   string Name,
                                   PrusaFirmwareHeader Header,
                                   long Size,
                                   DateTimeOffset UploadedAt);
