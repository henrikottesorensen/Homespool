using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
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
/// Checks that a file is a Prusa firmware image (<c>.bbf</c>), intact and signed with one key, down to
/// its last byte.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout</b>, from Prusa's <c>utils/pack_fw.py</c>: a 64-byte signature, the 32-byte SHA-256
/// it signs, a 480-byte header, the firmware, then entries of a type byte, a little-endian 32-bit
/// length and that many bytes. The signature and the digest cover the header and the firmware - bytes
/// 96 to the firmware's end - and nothing after.
/// </para>
/// <para>
/// <b>The entries are tied to the signature by the firmware itself.</b> The build writes each
/// tarball's SHA-256 into the firmware before it is signed (<c>cmake/Utilities.cmake</c>, the
/// <c>.resources_tarball_digest</c> and <c>.bootloader_tarball_digest</c> sections). So an image is
/// whole when it ends in exactly the resources tarball, its digest, the bootloader tarball and its
/// digest (types 9 to 12, in the order <c>pack_fw.py</c> writes them), each tarball hashes to its
/// digest entry, each digest occurs in the signed bytes, and the two digests differ - one tarball
/// twice would leave the printer no file for the other. Nothing else gets through: the printer
/// picks a <c>.bbf</c> by those unsigned digest entries, unpacks the tarball before it compares the
/// hash, and on a mismatch retries for ever (<c>src/resources/bootstrap.cpp</c>), so a changed tarball
/// would stop a printer until somebody removed the file at it. Where in the firmware a digest sits
/// is not checked: it differs between builds, and the printer looks both entries up by value, so
/// the two tarballs swapped with their digests still install.
/// </para>
/// <para>
/// <b>Releases before 6.6 pack their resources as littlefs images</b>, types 1 to 8: the resources
/// image, its block size, its block count and its content hash, then the same four for the bootloader.
/// Here the firmware has the content hash compiled in - a hash over the files inside, not the image's
/// bytes - and the printer mounts the image with the unsigned block size and count, copies the files
/// out, hashes its copy and compares, retrying for ever on a mismatch. So each image is read with
/// <see cref="LittlefsImage"/> under the size and count beside it, and its content hash must equal
/// the hash entry, which must occur in the signed bytes: everything the printer uses is then fixed by
/// them.
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

    /// <summary>Bytes before each entry's content: its type, then its length.</summary>
    private const int EntryHeaderLength = 5;

    /// <summary>
    /// The entries after the firmware since 6.6, in the order <c>pack_fw.py</c> writes them: the
    /// resources tarball, its digest, the bootloader tarball, its digest.
    /// </summary>
    private static readonly Entry[] TarballLayout =
    [
        new(9, EntryKind.Tarball), new(10, EntryKind.TarballDigest),
        new(11, EntryKind.Tarball), new(12, EntryKind.TarballDigest),
    ];

    /// <summary>
    /// The entries after the firmware before 6.6: for the resources and then the bootloader, a littlefs
    /// image, its block size, its block count and its content hash.
    /// </summary>
    private static readonly Entry[] ImageLayout =
    [
        new(1, EntryKind.Image), new(2, EntryKind.BlockSize), new(3, EntryKind.BlockCount), new(4, EntryKind.ImageHash),
        new(5, EntryKind.Image), new(6, EntryKind.BlockSize), new(7, EntryKind.BlockCount), new(8, EntryKind.ImageHash),
    ];

    /// <summary>What an entry after the firmware holds.</summary>
    private enum EntryKind
    {
        /// <summary>Never set.</summary>
        Undefined = 0,

        /// <summary>A tarball, which the next entry is the SHA-256 of.</summary>
        Tarball = 1,

        /// <summary>The SHA-256 of the tarball before it.</summary>
        TarballDigest = 2,

        /// <summary>A littlefs image, which the next three entries describe.</summary>
        Image = 3,

        /// <summary>The image's block size, a little-endian 32-bit number.</summary>
        BlockSize = 4,

        /// <summary>The image's block count, a little-endian 32-bit number.</summary>
        BlockCount = 5,

        /// <summary>The content hash of the files in the image.</summary>
        ImageHash = 6,
    }

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
    /// Reads <paramref name="content"/> from where it stands to its end, and says what it is.
    /// </summary>
    /// <param name="content">
    /// The image, positioned at its first byte. Must be seekable: the entries after the firmware are
    /// read first, so that one pass over the firmware both hashes it and finds their digests in it.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verdict, with the header whenever one could be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> cannot seek.</exception>
    public async Task<PrusaFirmwareCheck> CheckAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanSeek)
        {
            throw new ArgumentException("The image is read out of order, so the stream must seek.", nameof(content));
        }

        long start = content.Position;
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

        content.Seek(start + FirmwareOffset + header.FirmwareLength, SeekOrigin.Begin);
        Entries entries = await ReadEntriesAsync(content, cancellationToken);
        content.Seek(start + FirmwareOffset, SeekOrigin.Begin);

        DigestSearch search = new(entries.Digests);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(head.AsSpan(SignedFrom));
        search.Scan(head.AsSpan(SignedFrom));

        if (!await HashAsync(content, header.FirmwareLength, hash, search, cancellationToken))
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

        if (!Signs(signature, digest))
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.SignatureInvalid, header);
        }

        // The firmware is Prusa's from here on; what is left is whether everything after it is too.
        if (entries.Verdict != PrusaFirmwareVerdict.Verified)
        {
            return new PrusaFirmwareCheck(entries.Verdict, header);
        }

        if (!search.FoundAll)
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.ResourcesChanged, header);
        }

        return new PrusaFirmwareCheck(PrusaFirmwareVerdict.Verified, header, Base64Url.EncodeToString(digest));
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
    /// Reads the entries after the firmware, from where <paramref name="content"/> stands to its end:
    /// <see cref="PrusaFirmwareVerdict.Verified"/> with the two digests when they are exactly one of the
    /// two layouts and each tarball or image matches the digest after it.
    /// </summary>
    private static async Task<Entries> ReadEntriesAsync(Stream content, CancellationToken cancellationToken)
    {
        byte[] entryHeader = new byte[EntryHeaderLength];
        List<byte[]> digests = [];
        Entry[]? layout = null;

        // What the next digest entry must be; null for an image that could not be read.
        byte[]? expected = [];
        byte[] image = [];
        uint blockSize = 0;

        for (int i = 0; layout is null || i < layout.Length; i++)
        {
            int read = await content.ReadAtLeastAsync(entryHeader, EntryHeaderLength, throwOnEndOfStream: false, cancellationToken);

            if (read == 0)
            {
                return new Entries(PrusaFirmwareVerdict.ResourcesUnreadable, []);
            }

            if (read < EntryHeaderLength)
            {
                return new Entries(PrusaFirmwareVerdict.Truncated, []);
            }

            // The first entry says which layout this is.
            layout ??= entryHeader[0] == TarballLayout[0].Type ? TarballLayout :
                       entryHeader[0] == ImageLayout[0].Type ? ImageLayout :
                       null;

            if (layout is null || entryHeader[0] != layout[i].Type)
            {
                return new Entries(PrusaFirmwareVerdict.ResourcesUnreadable, []);
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(entryHeader.AsSpan(1));
            EntryKind kind = layout[i].Kind;

            if ((kind is EntryKind.Tarball or EntryKind.Image && length == 0) ||
                (kind is EntryKind.BlockSize or EntryKind.BlockCount && length != 4) ||
                (kind is EntryKind.TarballDigest or EntryKind.ImageHash && length != DigestLength))
            {
                return new Entries(PrusaFirmwareVerdict.ResourcesUnreadable, []);
            }

            if (length > content.Length - content.Position)
            {
                return new Entries(PrusaFirmwareVerdict.Truncated, []);
            }

            if (kind == EntryKind.Tarball)
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                if (!await HashAsync(content, length, hash, search: null, cancellationToken))
                {
                    return new Entries(PrusaFirmwareVerdict.Truncated, []);
                }

                expected = hash.GetHashAndReset();

                continue;
            }

            // Everything else is read whole: the digests and numbers are a few bytes, and an image is
            // walked as a filesystem, which a stream cannot be.
            byte[] value = new byte[length];
            await content.ReadExactlyAsync(value, cancellationToken);

            switch (kind)
            {
                case EntryKind.Image:
                    image = value;
                    break;

                case EntryKind.BlockSize:
                    blockSize = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;

                case EntryKind.BlockCount:
                    expected = LittlefsImage.ContentHash(image, blockSize, BinaryPrimitives.ReadUInt32LittleEndian(value));
                    break;

                default:
                    if (expected is null || !value.AsSpan().SequenceEqual(expected))
                    {
                        return new Entries(PrusaFirmwareVerdict.ResourcesChanged, []);
                    }

                    digests.Add(value);
                    break;
            }
        }

        if (await content.ReadAsync(entryHeader.AsMemory(0, 1), cancellationToken) != 0)
        {
            return new Entries(PrusaFirmwareVerdict.ResourcesUnreadable, []);
        }

        // The firmware names two different digests, the resources' and the bootloader's. A file carrying
        // one of the two twice passes everything above, and a printer needing the other finds no file
        // for it and waits in bootstrap for ever.
        if (digests[0].AsSpan().SequenceEqual(digests[1]))
        {
            return new Entries(PrusaFirmwareVerdict.ResourcesChanged, []);
        }

        return new Entries(PrusaFirmwareVerdict.Verified, digests);
    }

    /// <summary>
    /// Appends the next <paramref name="length"/> bytes to <paramref name="hash"/>, and shows them to
    /// <paramref name="search"/> when there is one; false when the stream ends first.
    /// </summary>
    private static async Task<bool> HashAsync(Stream content,
                                              long length,
                                              IncrementalHash hash,
                                              DigestSearch? search,
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
            search?.Scan(buffer.AsSpan(0, read));
            remaining -= read;
        }

        return true;
    }

    /// <summary>One entry of a layout: its type byte, and what it holds.</summary>
    private sealed record Entry(byte Type, EntryKind Kind);

    /// <summary>What the entries after the firmware came to, and the digests they carry.</summary>
    private sealed record Entries(PrusaFirmwareVerdict Verdict, IReadOnlyList<byte[]> Digests);

    /// <summary>
    /// Looks for each of a few digests anywhere in bytes shown to it a piece at a time, a digest
    /// split across two pieces included.
    /// </summary>
    private sealed class DigestSearch
    {
        private readonly IReadOnlyList<byte[]> _digests;
        private readonly bool[] _found;

        /// <summary>The last piece's final bytes, then the next piece: enough to see across the seam.</summary>
        private readonly byte[] _window = new byte[DigestLength - 1 + BufferSize];

        private int _carried;

        public DigestSearch(IReadOnlyList<byte[]> digests)
        {
            _digests = digests;
            _found = new bool[digests.Count];
        }

        public bool FoundAll => !_found.AsSpan().Contains(false);

        /// <summary>Looks through <paramref name="piece"/>, which is at most a buffer long.</summary>
        public void Scan(ReadOnlySpan<byte> piece)
        {
            piece.CopyTo(_window.AsSpan(_carried));
            ReadOnlySpan<byte> window = _window.AsSpan(0, _carried + piece.Length);

            for (int i = 0; i < _digests.Count; i++)
            {
                _found[i] |= window.IndexOf(_digests[i]) >= 0;
            }

            _carried = Math.Min(window.Length, DigestLength - 1);
            window[^_carried..].CopyTo(_window);
        }
    }
}
