using System;
using System.Security.Cryptography;
using System.Text;

namespace Homespool.FakePrinter;

/// <summary>
/// The immutable identity a Buddy printer presents on the wire: the fingerprint in its two lengths,
/// the serial number, the dotted printer-type code and the firmware version string.
/// </summary>
/// <remarks>
/// <para>
/// A real fingerprint is SHA-256 over (STM32 CPU UUID, factory MAC, serial number), emitted 5 bits
/// at a time into a 32-symbol alphabet - 50 characters of <c>0-9A-V</c>
/// (Prusa-Firmware-Buddy <c>src/common/support_utils.cpp</c>, <c>printerHash()</c>, at the pinned
/// ref <c>e96ce2b92</c>). <see cref="CreateRandom"/> draws 50 random symbols from that alphabet
/// instead of hashing anything - indistinguishable in shape, unrelated to any real board.
/// </para>
/// <para>
/// The firmware sends the fingerprint in <b>two lengths from one buffer</b>: all 50 characters in
/// <c>/p/register</c>'s JSON body, and only the first 16 in every HTTP header including the
/// <c>/p/ws</c> upgrade (<c>FINGERPRINT_HDR_SIZE</c>, <c>src/connect/printer.hpp</c> +
/// <c>connect.cpp:137,164</c>). <see cref="HeaderFingerprint"/> is that truncation; the fake
/// reproduces the asymmetry faithfully because the server's identity keying was once broken by
/// exactly this (see <c>notes/cross-channel-identity-bug.md</c>).
/// </para>
/// </remarks>
public sealed class PrinterIdentity
{
    /// <summary>The 5-bits-per-symbol alphabet <c>printerHash()</c> emits: digits then A-V.</summary>
    private const string FingerprintAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

    /// <summary>Full-length fingerprint as sent in <c>/p/register</c>'s body: 50 chars of 0-9A-V.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Serial number, e.g. <c>15715-4842441651816441</c> (shape from the real capture).</summary>
    public required string SerialNumber { get; init; }

    /// <summary>
    /// Dotted printer-type code, e.g. <c>1.3.5</c> for an MK3.5. The deprecated
    /// <c>type</c>/<c>version</c>/<c>subversion</c> triple is never sent (SDK <c>models.py</c>).
    /// </summary>
    public string PrinterType { get; init; } = "1.3.5";

    /// <summary>Firmware version string, e.g. <c>6.4.0+11974</c> (the capture printer's build).</summary>
    public string Firmware { get; init; } = "6.4.0+11974";

    /// <summary>
    /// The 16-character truncation every HTTP header carries (<c>FINGERPRINT_HDR_SIZE = 16</c>) -
    /// always an exact prefix of <see cref="Fingerprint"/>.
    /// </summary>
    public string HeaderFingerprint => Fingerprint[..16];

    /// <summary>
    /// A random identity: 50 fingerprint symbols and a plausible serial, both from a CSPRNG so two
    /// fakes created in the same tick can never collide.
    /// </summary>
    public static PrinterIdentity CreateRandom()
    {
        StringBuilder fingerprint = new(capacity: 50);

        for (int i = 0; i < 50; i++)
        {
            fingerprint.Append(FingerprintAlphabet[RandomNumberGenerator.GetInt32(FingerprintAlphabet.Length)]);
        }

        // Real serials look like "15715-4842441651816441": a 5-digit prefix, a dash, a long tail.
        string serial = FormattableString.Invariant(
            $"{RandomNumberGenerator.GetInt32(10000, 100000)}-{RandomNumberGenerator.GetInt32(0, int.MaxValue)}{RandomNumberGenerator.GetInt32(0, int.MaxValue)}");

        return new PrinterIdentity
        {
            Fingerprint = fingerprint.ToString(),
            SerialNumber = serial,
        };
    }
}
