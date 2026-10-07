using System;

namespace Homespool.Host.Firmware;

/// <summary>A stored firmware image, as a page lists it.</summary>
/// <param name="Digest">
/// The base64url SHA-384 of its bytes, which is also how a page names it: an image is its bytes, and
/// the same bytes uploaded twice are one image.
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
