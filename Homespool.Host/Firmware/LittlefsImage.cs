using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Homespool.Host.Firmware;

/// <summary>
/// A littlefs image read for one thing: the content hash Prusa's <c>utils/mklittlefs.py</c> computes
/// over it. Before 6.6, firmware compiles that hash in, copies the files out of the image in the
/// <c>.bbf</c>, hashes its copy the same way, and starts only when the two agree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ported from the littlefs firmware reads with</b>, 2.8 (on-disk 2.1), which Buddy 6.5.3 vendors
/// under <c>lib/Middlewares/Third_Party/littlefs</c>: <c>lfs_dir_fetchmatch</c> for which commits of a
/// metadata block count, <c>lfs_dir_getslice</c> for the latest value of a tag, <c>lfs_dir_rawread</c>
/// and <c>lfs_dir_getinfo</c> for a directory's entries, <c>lfs_ctz_find</c> for a file's blocks, and
/// <c>lfs_rawmount</c> for the superblock. Kept close to the C - names, order and integer widths - so
/// that it reads what the printer reads; a reader that disagreed with the printer about one image
/// would vouch for a file the printer then retries for ever.
/// </para>
/// <para>
/// <b>Stricter than littlefs where the printer's view could differ from this one</b>, refusing what an
/// image written by <c>mklittlefs.py</c> never holds: pending global state (a move or orphans
/// half-done), a name that is not plain ASCII or holds a <c>/</c>, entries out of littlefs's own name
/// order, a name without a structure, a directory reached twice, a file larger than the image. The
/// firmware hashes its copy in its own directory order, which is littlefs's name order, so an image
/// listing entries in any other order would hash differently on the printer than here.
/// </para>
/// <para>
/// <b>Bounded</b>: every read is checked against the image, metadata fetches and directory depth are
/// capped, and no file may claim more bytes than the image has, so a hostile image costs at most a few
/// passes over itself.
/// </para>
/// </remarks>
public sealed class LittlefsImage
{
    private const uint BlockNull = 0xffffffff;

    private const uint TypeReg = 0x001;
    private const uint TypeDir = 0x002;
    private const uint TypeSplice = 0x400;
    private const uint TypeName = 0x000;
    private const uint TypeStruct = 0x200;
    private const uint TypeTail = 0x600;
    private const uint TypeCreate = 0x401;
    private const uint TypeSuperblock = 0x0ff;
    private const uint TypeDirStruct = 0x200;
    private const uint TypeCtzStruct = 0x202;
    private const uint TypeInlineStruct = 0x201;
    private const uint TypeMoveState = 0x7ff;
    private const uint TypeCcrc = 0x500;

    /// <summary>The longest name firmware reads, <c>LFS_NAME_MAX</c>.</summary>
    private const uint NameMax = 255;

    /// <summary>Deeper than any resources tree; a directory pointing at an ancestor stops here at the latest.</summary>
    private const int MaxDepth = 16;

    /// <summary>What <see cref="GetSlice"/> answers for nothing found: the invalid tag, never a real one.</summary>
    private const uint NoEntry = 0xffffffff;

    /// <summary><c>lfs_crc</c>'s table: CRC-32 reflected, four bits at a time.</summary>
    private readonly ReadOnlyMemory<byte> _image;
    private readonly uint _blockSize;
    private readonly uint _blockCount;
    private readonly HashSet<BlockPair> _directoriesSeen = [];
    private uint _nameMax = NameMax;
    private long _fetchesLeft;
    private long _fileBytesLeft;
    private uint _counter;

    private LittlefsImage(ReadOnlyMemory<byte> image, uint blockSize, uint blockCount)
    {
        _image = image;
        _blockSize = blockSize;
        _blockCount = blockCount;
        _fetchesLeft = 4L * blockCount;
        _fileBytesLeft = image.Length;
    }

    /// <summary>
    /// The content hash of <paramref name="image"/> mounted with <paramref name="blockSize"/> and
    /// <paramref name="blockCount"/>, as <c>mklittlefs.py get-content-hash</c> computes it - or null when
    /// the image is not one this reads.
    /// </summary>
    public static byte[]? ContentHash(ReadOnlyMemory<byte> image, uint blockSize, uint blockCount)
    {
        // The printer's own configuration for the image: reads of one byte, a 16-byte cache, which
        // littlefs requires the block size to be a multiple of. And the image must be exactly its blocks,
        // because the printer reads a block from the file wherever block times size lands.
        if (blockSize < 128 || blockSize > 1024 * 1024 || blockSize % 16 != 0 || blockCount < 2 ||
            (ulong)blockSize * blockCount != (ulong)image.Length)
        {
            return null;
        }

        try
        {
            return new LittlefsImage(image, blockSize, blockCount).Hash();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private byte[] Hash()
    {
        BlockPair root = Mount();

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashDirectory(sha, root, "/", depth: 0);

        return sha.GetHashAndReset();
    }

    /// <summary><c>lfs_rawmount</c>: walks the tail list from blocks 0 and 1 for the superblock and the global state.</summary>
    private BlockPair Mount()
    {
        BlockPair tail = new(0, 1);
        BlockPair? root = null;
        uint[] gstate = new uint[3];

        while (tail.First != BlockNull && tail.Second != BlockNull)
        {
            MetadataDir dir = Fetch(tail);

            byte[] magic = new byte[8];
            uint tag = GetSlice(dir, MakeTag(0x7ff, 0x3ff, 0), MakeTag(TypeSuperblock, 0, 8), magic);

            if (tag != NoEntry)
            {
                if (Size(tag) != 8 || !"littlefs"u8.SequenceEqual(magic))
                {
                    throw new InvalidDataException();
                }

                root = dir.Pair;
                ReadSuperblock(dir);
            }

            byte[] delta = new byte[12];

            if (GetSlice(dir, MakeTag(0x7ff, 0, 0), MakeTag(TypeMoveState, 0, 12), delta) != NoEntry)
            {
                for (int i = 0; i < 3; i++)
                {
                    gstate[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(4 * i));
                }
            }

            tail = dir.Tail;
        }

        // A move or orphans left pending: littlefs would complete it on the first write, and reads
        // around it until then. An image written and closed by mklittlefs.py has none.
        if (root is null || gstate[0] != 0 || gstate[1] != 0 || gstate[2] != 0)
        {
            throw new InvalidDataException();
        }

        return root.Value;
    }

    private void ReadSuperblock(MetadataDir dir)
    {
        byte[] superblock = new byte[24];

        if (GetSlice(dir, MakeTag(0x7ff, 0x3ff, 0), MakeTag(TypeInlineStruct, 0, 24), superblock) == NoEntry)
        {
            throw new InvalidDataException();
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(superblock);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(4));
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(8));
        uint nameMax = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(12));
        uint fileMax = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(16));
        uint attrMax = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(20));

        if (version >> 16 != 2 || (version & 0xffff) > 1 ||
            nameMax > NameMax || fileMax > int.MaxValue || attrMax > 1022 ||
            blockSize != _blockSize || blockCount != _blockCount)
        {
            throw new InvalidDataException();
        }

        if (nameMax != 0)
        {
            _nameMax = nameMax;
        }
    }

    private void HashDirectory(IncrementalHash sha, BlockPair pair, string path, int depth)
    {
        if (depth > MaxDepth || !_directoriesSeen.Add(pair.First < pair.Second ? pair : new BlockPair(pair.Second, pair.First)))
        {
            throw new InvalidDataException();
        }

        Mark(sha);
        sha.AppendData(Encoding.ASCII.GetBytes(path));
        Mark(sha);

        foreach (Entry entry in ReadDirectory(pair))
        {
            string child = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;

            if (entry.Type == TypeDir)
            {
                HashDirectory(sha, entry.Directory, child, depth + 1);
            }
            else
            {
                Mark(sha);
                sha.AppendData(Encoding.ASCII.GetBytes(child));
                Mark(sha);
                AppendFile(sha, entry);
                Mark(sha);
            }
        }

        Mark(sha);
    }

    private void Mark(IncrementalHash sha)
    {
        Span<byte> counter = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, _counter++);
        sha.AppendData(counter);
    }

    private void AppendFile(IncrementalHash sha, Entry entry)
    {
        _fileBytesLeft -= entry.Size;

        if (_fileBytesLeft < 0)
        {
            throw new InvalidDataException();
        }

        if (entry.Inline is not null)
        {
            sha.AppendData(entry.Inline);

            return;
        }

        // lfs_file_flushedread: a block at a time, each found from the head by lfs_ctz_find.
        uint pos = 0;

        while (pos < entry.Size)
        {
            uint block = CtzFind(entry.Head, entry.Size, pos, out uint off);
            uint diff = Math.Min(entry.Size - pos, _blockSize - off);

            // Never for an offset lfs_ctz_index gives; a read that cannot move would never end.
            if (diff == 0)
            {
                throw new InvalidDataException();
            }

            sha.AppendData(Read(block, off, diff));
            pos += diff;
        }
    }

    /// <summary>
    /// <c>lfs_dir_rawread</c> without its <c>.</c> and <c>..</c>: each id of each metadata pair the
    /// directory spans, skipping the ones with no name - and refusing any that are out of order.
    /// </summary>
    private List<Entry> ReadDirectory(BlockPair pair)
    {
        List<Entry> entries = [];
        MetadataDir dir = Fetch(pair);
        uint id = 0;

        while (true)
        {
            if (id == dir.Count)
            {
                if (!dir.Split)
                {
                    break;
                }

                dir = Fetch(dir.Tail);
                id = 0;

                continue;
            }

            if (GetInfo(dir, id) is Entry entry)
            {
                if (entries.Count > 0 && CompareNames(entries[^1].NameBytes, entry.NameBytes) >= 0)
                {
                    throw new InvalidDataException();
                }

                entries.Add(entry);
            }

            id += 1;
        }

        return entries;
    }

    /// <summary><c>lfs_dir_getinfo</c>, and the structure behind it; null for an id with no name.</summary>
    private Entry? GetInfo(MetadataDir dir, uint id)
    {
        // The inline read masks ids to nine bits (lfs_file_flushedread), so past 511 two entries could
        // share one file's bytes. No resources directory comes near.
        if (id >= 0x1ff)
        {
            throw new InvalidDataException();
        }

        byte[] name = new byte[_nameMax + 1];
        uint nameTag = GetSlice(dir, MakeTag(0x780, 0x3ff, 0), MakeTag(TypeName, id, _nameMax + 1), name);

        if (nameTag == NoEntry)
        {
            return null;
        }

        uint type = Type3(nameTag);
        uint length = Size(nameTag);

        if ((type != TypeReg && type != TypeDir) || length == 0 || length > _nameMax ||
            !IsPlainName(name.AsSpan(0, (int)length)))
        {
            throw new InvalidDataException();
        }

        byte[] nameBytes = name[..(int)length];
        byte[] structure = new byte[8];
        uint structTag = GetSlice(dir, MakeTag(0x700, 0x3ff, 0), MakeTag(TypeStruct, id, 8), structure);

        if (structTag == NoEntry)
        {
            throw new InvalidDataException();
        }

        uint structType = Type3(structTag);
        string text = Encoding.ASCII.GetString(nameBytes);

        if (type == TypeDir)
        {
            if (structType != TypeDirStruct || Size(structTag) != 8)
            {
                throw new InvalidDataException();
            }

            return new Entry(text, nameBytes, TypeDir, new BlockPair(Le32(structure, 0), Le32(structure, 4)), 0, 0, null);
        }

        if (structType == TypeCtzStruct && Size(structTag) == 8)
        {
            uint head = Le32(structure, 0);
            uint size = Le32(structure, 4);

            if (size > (uint)_image.Length)
            {
                throw new InvalidDataException();
            }

            return new Entry(text, nameBytes, TypeReg, default, head, size, null);
        }

        if (structType == TypeInlineStruct)
        {
            // lfs_file_rawopencfg's size, read the way lfs_file_flushedread reads an inline file.
            uint size = Size(structTag);
            byte[] data = new byte[size];

            if (size > 0 &&
                GetSlice(dir, MakeTag(0xfff, 0x1ff, 0), MakeTag(TypeInlineStruct, id, 0), data, gsize: size) == NoEntry)
            {
                throw new InvalidDataException();
            }

            return new Entry(text, nameBytes, TypeReg, default, 0, size, data);
        }

        throw new InvalidDataException();
    }

    /// <summary>Printable ASCII, never <c>/</c>, never <c>.</c> or <c>..</c> - the names a path can carry unchanged.</summary>
    private static bool IsPlainName(ReadOnlySpan<byte> name)
    {
        foreach (byte b in name)
        {
            if (b < 0x20 || b > 0x7e || b == (byte)'/')
            {
                return false;
            }
        }

        return !name.SequenceEqual("."u8) && !name.SequenceEqual(".."u8);
    }

    /// <summary><c>lfs_dir_find_match</c>'s order: bytes, then the shorter first.</summary>
    private static int CompareNames(byte[] a, byte[] b)
    {
        int bytes = a.AsSpan(0, Math.Min(a.Length, b.Length)).SequenceCompareTo(b.AsSpan(0, Math.Min(a.Length, b.Length)));

        return bytes != 0 ? bytes : a.Length.CompareTo(b.Length);
    }

    /// <summary><c>lfs_dir_fetchmatch</c> matching nothing: which block of the pair, and how far its commits are good.</summary>
    private MetadataDir Fetch(BlockPair pair)
    {
        if (pair.First >= _blockCount || pair.Second >= _blockCount || --_fetchesLeft < 0)
        {
            throw new InvalidDataException();
        }

        uint[] blocks = [pair.First, pair.Second];
        uint[] revs = [Le32(Read(blocks[0], 0, 4), 0), Le32(Read(blocks[1], 0, 4), 0)];

        // The C reads revs[1] after comparing against it, while it is still zero; only the second
        // comparison can move the choice off the first block.
        int r = Scmp(revs[1], revs[0]) > 0 ? 1 : 0;

        uint first = blocks[r];
        uint second = blocks[1 - r];
        uint rev = revs[r];

        for (int attempt = 0; attempt < 2; attempt++)
        {
            MetadataDir? found = Scan(first, second, rev);

            if (found is not null)
            {
                return found;
            }

            (first, second) = (second, first);
            rev = revs[1 - r];
        }

        throw new InvalidDataException();
    }

    /// <summary>One block's log, as the fetch loop walks it: the state at its last commit whose CRC holds, or null for none.</summary>
    private MetadataDir? Scan(uint block, uint other, uint rev)
    {
        MetadataDir? committed = null;

        uint off = 0;
        uint ptag = 0xffffffff;
        ushort tempCount = 0;
        BlockPair tempTail = new(BlockNull, BlockNull);
        bool tempSplit = false;

        Span<byte> revBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(revBytes, rev);
        uint crc = Crc(0xffffffff, revBytes);

        while (true)
        {
            off += DSize(ptag);

            if ((ulong)off + 4 > _blockSize)
            {
                break;
            }

            ReadOnlySpan<byte> raw = Read(block, off, 4);
            crc = Crc(crc, raw);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(raw) ^ ptag;

            if (!IsValid(tag) || (ulong)off + DSize(tag) > _blockSize)
            {
                break;
            }

            ptag = tag;

            if (Type2(tag) == TypeCcrc)
            {
                if ((ulong)off + 8 > _blockSize)
                {
                    break;
                }

                if (crc != Le32(Read(block, off + 4, 4), 0))
                {
                    break;
                }

                ptag ^= (Chunk(tag) & 1U) << 31;
                committed = new MetadataDir(new BlockPair(block, other), off + DSize(tag), ptag, tempCount, tempTail, tempSplit);
                crc = 0xffffffff;

                continue;
            }

            crc = Crc(crc, Read(block, off + 4, DSize(tag) - 4));

            if (Type1(tag) == TypeName)
            {
                if (Id(tag) >= tempCount)
                {
                    tempCount = (ushort)(Id(tag) + 1);
                }
            }
            else if (Type1(tag) == TypeSplice)
            {
                tempCount = unchecked((ushort)(tempCount + Splice(tag)));
            }
            else if (Type1(tag) == TypeTail)
            {
                if ((ulong)off + 12 > _blockSize)
                {
                    break;
                }

                tempSplit = (Chunk(tag) & 1) != 0;
                ReadOnlySpan<byte> pointer = Read(block, off + 4, 8);
                tempTail = new BlockPair(Le32(pointer, 0), Le32(pointer, 4));
            }
        }

        return committed;
    }

    /// <summary>
    /// <c>lfs_dir_getslice</c>: the most recent tag matching <paramref name="gtag"/> under
    /// <paramref name="gmask"/>, walking the good commits backwards and following ids across creates
    /// and deletes, with its data copied into <paramref name="buffer"/> - or <see cref="NoEntry"/>.
    /// </summary>
    private uint GetSlice(MetadataDir dir, uint gmask, uint gtag, Span<byte> buffer, uint? gsize = null)
    {
        uint size = gsize ?? (uint)buffer.Length;
        uint off = dir.Off;
        uint ntag = dir.ETag;
        uint gdiff = 0;

        while (off >= 4 + DSize(ntag))
        {
            off -= DSize(ntag);
            uint tag = ntag;
            ntag = (BinaryPrimitives.ReadUInt32BigEndian(Read(dir.Pair.First, off, 4)) ^ tag) & 0x7fffffff;

            if (Id(gmask) != 0 && Type1(tag) == TypeSplice && Id(tag) <= Id(unchecked(gtag - gdiff)))
            {
                if (tag == (MakeTag(TypeCreate, 0, 0) | (MakeTag(0, 0x3ff, 0) & unchecked(gtag - gdiff))))
                {
                    return NoEntry;
                }

                gdiff = unchecked(gdiff + ((uint)Splice(tag) << 10));
            }

            if ((gmask & tag) == (gmask & unchecked(gtag - gdiff)))
            {
                if (IsDelete(tag))
                {
                    return NoEntry;
                }

                uint diff = Math.Min(Size(tag), size);
                buffer.Clear();
                Read(dir.Pair.First, off + 4, diff).CopyTo(buffer);

                uint found = unchecked(tag + gdiff);

                // Negative is an error code in the C; a found tag never is.
                return IsValid(found) ? found : throw new InvalidDataException();
            }
        }

        return NoEntry;
    }

    /// <summary><c>lfs_ctz_find</c>: the block holding <paramref name="pos"/>, and the offset in it.</summary>
    private uint CtzFind(uint head, uint size, uint pos, out uint off)
    {
        uint current = CtzIndex(size - 1, out _);
        uint target = CtzIndex(pos, out off);

        while (current > target)
        {
            int skip = Math.Min(Npw2(current - target + 1) - 1, BitOperations.TrailingZeroCount(current));
            head = Le32(Read(head, (uint)(4 * skip), 4), 0);
            current -= 1U << skip;
        }

        return head;
    }

    /// <summary><c>lfs_ctz_index</c>: which block of the skip-list an offset falls in, and where in it.</summary>
    private uint CtzIndex(uint offset, out uint within)
    {
        uint b = _blockSize - (2 * 4);
        uint i = offset / b;

        if (i == 0)
        {
            within = offset;

            return 0;
        }

        i = (offset - (4 * ((uint)BitOperations.PopCount(i - 1) + 2))) / b;
        within = offset - (b * i) - (4 * (uint)BitOperations.PopCount(i));

        return i;
    }

    private static int Npw2(uint a)
    {
        return 32 - BitOperations.LeadingZeroCount(a - 1);
    }

    /// <summary><c>lfs_bd_read</c>'s bounds: a block that exists, and bytes inside it.</summary>
    private ReadOnlySpan<byte> Read(uint block, uint off, uint size)
    {
        if (block >= _blockCount || (ulong)off + size > _blockSize)
        {
            throw new InvalidDataException();
        }

        return _image.Span.Slice((int)((block * (long)_blockSize) + off), (int)size);
    }

    private static uint Le32(ReadOnlySpan<byte> bytes, int at)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    }

    /// <summary><c>lfs_crc</c>: CRC-32, reflected, from the seed given, with no final inversion.</summary>
    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (crc >> 4) ^ CrcTable[(int)((crc ^ (uint)(b >> 0)) & 0xf)];
            crc = (crc >> 4) ^ CrcTable[(int)((crc ^ (uint)(b >> 4)) & 0xf)];
        }

        return crc;
    }

    private static readonly uint[] CrcTable =
    [
        0x00000000, 0x1db71064, 0x3b6e20c8, 0x26d930ac, 0x76dc4190, 0x6b6b51f4, 0x4db26158, 0x5005713c,
        0xedb88320, 0xf00f9344, 0xd6d6a3e8, 0xcb61b38c, 0x9b64c2b0, 0x86d3d2d4, 0xa00ae278, 0xbdbdf21c,
    ];

    private static uint MakeTag(uint type, uint id, uint size)
    {
        return (type << 20) | (id << 10) | size;
    }

    private static bool IsValid(uint tag)
    {
        return (tag & 0x80000000) == 0;
    }

    private static bool IsDelete(uint tag)
    {
        return ((int)(tag << 22) >> 22) == -1;
    }

    private static uint Type1(uint tag)
    {
        return (tag & 0x70000000) >> 20;
    }

    private static uint Type2(uint tag)
    {
        return (tag & 0x78000000) >> 20;
    }

    private static uint Type3(uint tag)
    {
        return (tag & 0x7ff00000) >> 20;
    }

    private static uint Chunk(uint tag)
    {
        return (tag & 0x0ff00000) >> 20;
    }

    private static int Splice(uint tag)
    {
        return (sbyte)Chunk(tag);
    }

    private static uint Id(uint tag)
    {
        return (tag & 0x000ffc00) >> 10;
    }

    private static uint Size(uint tag)
    {
        return tag & 0x000003ff;
    }

    private static uint DSize(uint tag)
    {
        return 4 + Size(unchecked(tag + (IsDelete(tag) ? 1U : 0U)));
    }

    private static int Scmp(uint a, uint b)
    {
        return unchecked((int)(a - b));
    }

    /// <summary>A metadata pair as fetched: <c>lfs_mdir_t</c>'s fields that reads use.</summary>
    private sealed record MetadataDir(BlockPair Pair, uint Off, uint ETag, ushort Count, BlockPair Tail, bool Split);

    /// <summary>A metadata pair's two blocks, the first the one to read.</summary>
    private readonly record struct BlockPair(uint First, uint Second);

    /// <summary>A directory entry: a directory and its pair, or a file and its skip-list or inline bytes.</summary>
    private sealed record Entry(string Name, byte[] NameBytes, uint Type, BlockPair Directory, uint Head, uint Size, byte[]? Inline);
}
