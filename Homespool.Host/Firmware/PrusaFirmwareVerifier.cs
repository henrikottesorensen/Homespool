using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace Homespool.Host.Firmware;

/// <summary>
/// Checks that a file is a Prusa firmware image (<c>.bbf</c>), intact and signed with one key.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout</b>, from Prusa's <c>utils/pack_fw.py</c>: a 64-byte signature, the 32-byte SHA-256
/// it signs, a 480-byte header, the firmware, then TLV entries (resources and the bootloader). The
/// signature and the digest cover the header and the firmware - bytes 96 to the firmware's end - and
/// nothing after. The TLVs are covered all the same, one step removed: the signed firmware carries
/// the revision of the resources it expects, and refuses any other when it installs them.
/// </para>
/// <para>
/// <b>Why Homespool checks at all, when the printer's bootloader does.</b> The bootloader checks only while
/// the printer's appendix is intact. Once it is broken, the printer flashes anything - so for such a
/// printer this check is the only one there is, and it refuses what is not Prusa's whatever the
/// printer would accept.
/// </para>
/// <para>
/// <b>ECDSA over secp256k1, the signature as big-endian <c>r ‖ s</c>.</b> <c>pack_fw.py</c> signs with
/// a key it reads from a PEM that is not published, so the curve was established from the files: of
/// the curves and byte orders tried, only secp256k1 with big-endian <c>r ‖ s</c> recovers a single
/// public key common to several independently signed releases, and that key appears verbatim in every
/// Buddy bootloader Prusa ship.
/// BouncyCastle does the arithmetic because the framework's <c>ECDsa</c> has no secp256k1 on macOS.
/// </para>
/// </remarks>
public sealed class PrusaFirmwareVerifier
{
    /// <summary>Bytes of signature at the start of the file.</summary>
    public const int SignatureLength = 64;

    /// <summary>Bytes of SHA-256 after the signature.</summary>
    public const int DigestLength = 32;

    /// <summary>Where the signed region starts: the header, after the signature and its digest.</summary>
    public const int SignedFrom = SignatureLength + DigestLength;

    /// <summary>Where the firmware starts: after the signature, its digest and the 480-byte header.</summary>
    public const int FirmwareOffset = 576;

    /// <summary>The only header version the verifier reads, and the one every current Buddy image carries.</summary>
    public const int BbfVersion = 2;

    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Prusa's signing key, x then y, big-endian - exactly as it sits in each Buddy bootloader.
    /// </summary>
    private static readonly byte[] PrusaKey = Convert.FromHexString(
        "183e29a1660fed85df4446ebe7a0e29afc3f36fd240f8472b2c088526bc0ec33" +
        "1fbf5a7b7557495358ae6dd17afa77afe896922b73f3cef846bce551c8121621");

    private readonly ECPublicKeyParameters _key;

    /// <summary>A verifier holding <paramref name="key"/>: a secp256k1 public key, x then y, big-endian.</summary>
    /// <exception cref="ArgumentException">The key is not 64 bytes, or not a point on the curve.</exception>
    public PrusaFirmwareVerifier(ReadOnlySpan<byte> key)
    {
        if (key.Length != 64)
        {
            throw new ArgumentException("A secp256k1 public key is 64 bytes, x then y.", nameof(key));
        }

        X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
        byte[] uncompressed = new byte[65];
        uncompressed[0] = 0x04;
        key.CopyTo(uncompressed.AsSpan(1));

        try
        {
            _key = new ECPublicKeyParameters(curve.Curve.DecodePoint(uncompressed),
                                             new ECDomainParameters(curve));
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException("The key is not a point on secp256k1.", nameof(key), e);
        }
    }

    /// <summary>The verifier holding Prusa's key.</summary>
    public static PrusaFirmwareVerifier Prusa { get; } = new(PrusaKey);

    /// <summary>
    /// Reads <paramref name="content"/> from where it stands through the end of the firmware, and says
    /// what it is.
    /// </summary>
    /// <param name="content">The image, positioned at its first byte. Need not be seekable.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verdict, with the header whenever one could be read.</returns>
    public async Task<PrusaFirmwareCheck> CheckAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] head = new byte[FirmwareOffset];

        if (await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken) < head.Length)
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.NotAnImage, Header: null);
        }

        if (head[SignedFrom + 16] != BbfVersion)
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.NotAnImage, Header: null);
        }

        PrusaFirmwareHeader header = ReadHeader(head);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(head.AsSpan(SignedFrom));

        if (!await HashFirmwareAsync(content, header.FirmwareLength, hash, cancellationToken))
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.Truncated, header);
        }

        byte[] digest = hash.GetHashAndReset();

        if (!digest.AsSpan().SequenceEqual(head.AsSpan(SignatureLength, DigestLength)))
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.Damaged, header);
        }

        ReadOnlySpan<byte> signature = head.AsSpan(0, SignatureLength);

        if (!signature.ContainsAnyExcept((byte)0))
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.NoSignature, header);
        }

        PrusaFirmwareVerdict verdict = Signs(signature, digest) ?
            PrusaFirmwareVerdict.Verified :
            PrusaFirmwareVerdict.SignatureInvalid;

        return new PrusaFirmwareCheck(verdict, header);
    }

    /// <summary>
    /// Whether <paramref name="signature"/>, <c>r ‖ s</c> big-endian, is this verifier's key signing
    /// <paramref name="digest"/>.
    /// </summary>
    /// <remarks>
    /// The one step <see cref="CheckAsync"/> takes that needs the key, and public so that it can be
    /// checked against a real release's signature without the release itself.
    /// </remarks>
    public bool Signs(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> digest)
    {
        if (signature.Length != SignatureLength || digest.Length != DigestLength)
        {
            return false;
        }

        ECDsaSigner signer = new();
        signer.Init(forSigning: false, _key);

        return signer.VerifySignature(digest.ToArray(),
                                      new BigInteger(1, signature[..32].ToArray()),
                                      new BigInteger(1, signature[32..].ToArray()));
    }

    private static PrusaFirmwareHeader ReadHeader(byte[] head)
    {
        ReadOnlySpan<byte> fields = head.AsSpan(SignedFrom);

        return new PrusaFirmwareHeader(Major: fields[4],
                                       Minor: fields[5],
                                       Patch: fields[6],
                                       Build: BinaryPrimitives.ReadUInt16LittleEndian(fields[7..]),
                                       Prerelease: Encoding.ASCII.GetString(fields.Slice(9, 5)).TrimEnd('\0'),
                                       Board: fields[14],
                                       PrinterType: fields[15],
                                       PrinterVersion: fields[18],
                                       PrinterSubversion: fields[17],
                                       FirmwareLength: BinaryPrimitives.ReadUInt32LittleEndian(fields));
    }

    /// <summary>
    /// Appends the next <paramref name="length"/> bytes to <paramref name="hash"/>; false when the
    /// stream ends first.
    /// </summary>
    private static async Task<bool> HashFirmwareAsync(Stream content,
                                                      long length,
                                                      IncrementalHash hash,
                                                      CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long remaining = length;

        while (remaining > 0)
        {
            int read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                                               cancellationToken);

            if (read == 0)
            {
                return false;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        return true;
    }
}
