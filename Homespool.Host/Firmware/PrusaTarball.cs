using System;

namespace Homespool.Host.Firmware;

/// <summary>
/// Whether bytes are a tarball as a 6.6 or later printer unpacks one from a <c>.bbf</c>: ustar headers
/// for files and directories, each file's content after it, and zero blocks to close.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tarball's shape is checked, when its hash already is.</b> A tarball is accepted when its
/// SHA-256 is a digest the signed firmware names - and a pre-6.6 firmware names two SHA-256s over
/// streams anyone can rebuild (its littlefs images' content hashes: marks, paths and file bytes). Those
/// streams would pass as tarballs, and a printer on that firmware, looking for littlefs images, would
/// find none and wait in bootstrap for ever. A stream of that kind opens with a zero counter where a
/// ustar header would be, so it is not a tarball; a real tarball with the same hash would be a SHA-256
/// preimage. With this, each layout's entries are checked to be what that layout's printer reads.
/// </para>
/// <para>
/// <b>Read as Buddy's extractor reads it</b> (<c>src/resources/tarball.cpp</c>): 512-byte blocks, the
/// length a whole number of them; a header with <c>ustar</c> at offset 257 and a checksum that holds,
/// summed with its own field as spaces; type <c>0</c> or <c>5</c>, anything else refused as the
/// extractor refuses it; a name rooted at <c>/</c>, within the 100-byte field, no prefix, no
/// <c>..</c>. <b>Stricter</b> where the extractor would pass over a block that is not a header: every
/// block here is a header, a file's content, or one of the closing zero blocks, and at least one
/// member comes before them. Prusa's tarballs are all of that.
/// </para>
/// </remarks>
public static class PrusaTarball
{
    private const int BlockSize = 512;
    private const int NameOffset = 0;
    private const int NameLength = 100;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;
    private const int ChecksumOffset = 148;
    private const int ChecksumLength = 8;
    private const int TypeOffset = 156;
    private const int MagicOffset = 257;
    private const int PrefixOffset = 345;

    /// <summary>Whether <paramref name="tarball"/> is one a printer unpacks, block for block.</summary>
    public static bool IsWellFormed(ReadOnlySpan<byte> tarball)
    {
        if (tarball.IsEmpty || tarball.Length % BlockSize != 0)
        {
            return false;
        }

        int members = 0;
        int at = 0;

        while (at < tarball.Length)
        {
            ReadOnlySpan<byte> block = tarball.Slice(at, BlockSize);

            // The close: zero blocks, and nothing after them but more.
            if (!block.ContainsAnyExcept((byte)0))
            {
                return members > 0 && !tarball[at..].ContainsAnyExcept((byte)0);
            }

            if (!block.Slice(MagicOffset, 5).SequenceEqual("ustar"u8) || !ChecksumHolds(block) || !NameIsSafe(block))
            {
                return false;
            }

            long content;

            switch (block[TypeOffset])
            {
                case (byte)'0':
                    if (!TryParseOctal(block.Slice(SizeOffset, SizeLength), out uint size))
                    {
                        return false;
                    }

                    content = (size + (long)BlockSize - 1) / BlockSize * BlockSize;
                    break;

                case (byte)'5':
                    content = 0;
                    break;

                default:
                    return false;
            }

            if (at + BlockSize + content > tarball.Length)
            {
                return false;
            }

            at += BlockSize + (int)content;
            members++;
        }

        // Members to the very end, and no zero blocks to close.
        return false;
    }

    /// <summary>The sum of the header's bytes, its checksum field counted as spaces, against that field.</summary>
    private static bool ChecksumHolds(ReadOnlySpan<byte> block)
    {
        uint sum = 0;

        for (int i = 0; i < BlockSize; i++)
        {
            sum += i is >= ChecksumOffset and < ChecksumOffset + ChecksumLength ? (byte)' ' : block[i];
        }

        return TryParseOctal(block.Slice(ChecksumOffset, ChecksumLength), out uint stored) && stored == sum;
    }

    /// <summary>The extractor's <c>build_path</c> rules: rooted, within the name field, no prefix, no <c>..</c>.</summary>
    private static bool NameIsSafe(ReadOnlySpan<byte> block)
    {
        ReadOnlySpan<byte> field = block.Slice(NameOffset, NameLength);
        int end = field.IndexOf((byte)0);
        ReadOnlySpan<byte> name = end < 0 ? field : field[..end];

        if (name.IsEmpty || name[0] != (byte)'/' || block[PrefixOffset] != 0)
        {
            return false;
        }

        foreach (Range part in name.Split((byte)'/'))
        {
            if (name[part].SequenceEqual(".."u8))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The extractor's <c>parse_octal</c>: spaces and NULs before and after, octal digits between, and
    /// nothing else.
    /// </summary>
    private static bool TryParseOctal(ReadOnlySpan<byte> field, out uint value)
    {
        value = 0;
        int i = 0;

        while (i < field.Length && field[i] is (byte)' ' or 0)
        {
            i++;
        }

        for (; i < field.Length; i++)
        {
            byte c = field[i];

            if (c is (byte)' ' or 0)
            {
                break;
            }

            if (c is < (byte)'0' or > (byte)'7')
            {
                return false;
            }

            value = (value << 3) | (uint)(c - '0');
        }

        return true;
    }
}
