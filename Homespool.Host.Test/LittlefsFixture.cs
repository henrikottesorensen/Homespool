using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Homespool.Host.Test;

/// <summary>
/// One of the littlefs images under <c>littlefs/</c>, with the block size and count it was written
/// with and the content hash the littlefs that wrote it gives - see <c>tools/littlefs-fixtures</c>.
/// </summary>
/// <param name="Name">The image's file name.</param>
/// <param name="Image">The image's bytes.</param>
/// <param name="BlockSize">The block size it was written with.</param>
/// <param name="BlockCount">The block count it was written with.</param>
/// <param name="Hash">Its content hash, as <c>mklittlefs.py</c> computes it.</param>
internal sealed record LittlefsFixture(string Name, byte[] Image, uint BlockSize, uint BlockCount, byte[] Hash)
{
    /// <summary>A tree of directories and files, one directory split across metadata pairs.</summary>
    public static LittlefsFixture Tree => All.Single(fixture => fixture.Name == "tree.img");

    /// <summary>A few names written, removed and written again.</summary>
    public static LittlefsFixture Rewritten => All.Single(fixture => fixture.Name == "rewritten.img");

    /// <summary>Every fixture <c>fixtures.json</c> lists.</summary>
    public static IReadOnlyList<LittlefsFixture> All { get; } = Load();

    /// <summary>An image littlefs wrote with a file name that is not plain ASCII.</summary>
    public static byte[] NonAsciiName => File.ReadAllBytes(PathOf("non-ascii-name.img"));

    /// <summary>An image littlefs wrote with 200-byte blocks, 32 of them, which the printer's cache cannot take.</summary>
    public static byte[] OddBlockSize => File.ReadAllBytes(PathOf("odd-block-size.img"));

    private static List<LittlefsFixture> Load()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PathOf("fixtures.json")));

        return
        [
            .. document.RootElement.EnumerateArray().Select(entry => new LittlefsFixture(
                entry.GetProperty("image").GetString()!,
                File.ReadAllBytes(PathOf(entry.GetProperty("image").GetString()!)),
                entry.GetProperty("blockSize").GetUInt32(),
                entry.GetProperty("blockCount").GetUInt32(),
                Convert.FromHexString(entry.GetProperty("hash").GetString()!))),
        ];
    }

    private static string PathOf(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "littlefs", name);
    }
}
