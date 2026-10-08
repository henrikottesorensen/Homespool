using System;
using System.Buffers.Binary;

namespace Homespool.Host.Test;

/// <summary>
/// Edits to a littlefs image that littlefs itself would never write - a name out of order, another
/// magic, a newer version - made by changing bytes in a metadata block and sealing its commits again,
/// so the reader sees a log whose CRCs hold.
/// </summary>
internal static class LittlefsEdits
{
    /// <summary>
    /// The image of <paramref name="fixture"/> with every occurrence of <paramref name="find"/> in its
    /// metadata blocks replaced by <paramref name="replace"/>, of the same length, and those blocks'
    /// commits sealed again.
    /// </summary>
    public static byte[] Replace(LittlefsFixture fixture, ReadOnlySpan<byte> find, ReadOnlySpan<byte> replace)
    {
        if (find.Length != replace.Length)
        {
            throw new ArgumentException("A replacement keeps the length.", nameof(replace));
        }

        byte[] image = (byte[])fixture.Image.Clone();
        int blockSize = (int)fixture.BlockSize;
        bool replacedAny = false;

        for (int block = 0; block < fixture.BlockCount; block++)
        {
            Span<byte> bytes = image.AsSpan(block * blockSize, blockSize);

            if (!IsMetadata(bytes))
            {
                continue;
            }

            bool replaced = false;
            int at;

            while ((at = bytes.IndexOf(find)) >= 0)
            {
                replace.CopyTo(bytes[at..]);
                replaced = true;
            }

            if (replaced)
            {
                Seal(bytes);
                replacedAny = true;
            }
        }

        if (!replacedAny)
        {
            throw new InvalidOperationException("Nothing in the metadata to replace.");
        }

        return image;
    }

    /// <summary>A block whose first tag, after its revision count, is a valid one: a metadata block.</summary>
    private static bool IsMetadata(ReadOnlySpan<byte> block)
    {
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(block[4..]) ^ 0xffffffff;

        return (tag & 0x80000000) == 0 && tag != 0 && block[..4].IndexOfAnyExcept((byte)0xff) >= 0;
    }

    /// <summary>Writes each commit's CRC again over the bytes it now covers, as lfs_dir_commitcrc does.</summary>
    private static void Seal(Span<byte> block)
    {
        uint crc = Crc(0xffffffff, block[..4]);
        uint ptag = 0xffffffff;
        int off = 4;

        while (off + 4 <= block.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(block[off..]) ^ ptag;

            if ((tag & 0x80000000) != 0)
            {
                return;
            }

            int size = DSize(tag);

            if (off + size > block.Length)
            {
                return;
            }

            crc = Crc(crc, block.Slice(off, 4));

            if ((tag & 0x78000000) >> 20 == 0x500)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block[(off + 4)..], crc);
                ptag = tag ^ ((((tag & 0x0ff00000) >> 20) & 1U) << 31);
                crc = 0xffffffff;
            }
            else
            {
                crc = Crc(crc, block.Slice(off + 4, size - 4));
                ptag = tag;
            }

            off += size;
        }
    }

    private static int DSize(uint tag)
    {
        bool delete = ((int)(tag << 22) >> 22) == -1;

        return 4 + (int)(unchecked(tag + (delete ? 1U : 0U)) & 0x3ff);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc ^ (uint)(b >> bit)) & 1) * 0xedb88320;
            }
        }

        return crc;
    }
}
