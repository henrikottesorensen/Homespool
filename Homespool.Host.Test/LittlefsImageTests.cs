using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Firmware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="LittlefsImage"/> - the content hash of a littlefs image, as the printer will compute it
/// over the files it copies out.
/// </summary>
/// <remarks>
/// The fixtures were written by the littlefs Buddy 6.5.3 vendors, and their hashes computed by it, so
/// agreeing with them is agreeing with the C this reader is ported from. Prusa's own images are checked
/// too, wherever the repository's <c>firmware/</c> directory holds one.
/// </remarks>
public sealed class LittlefsImageTests
{
    public static TheoryData<string> Fixtures => [.. LittlefsFixture.All.Select(fixture => fixture.Name)];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EachFixtureHashesAsTheLittlefsThatWroteIt(string name)
    {
        // Arrange
        LittlefsFixture fixture = LittlefsFixture.All.Single(candidate => candidate.Name == name);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(fixture.Image, fixture.BlockSize, fixture.BlockCount);

        // Assert
        hash.Should().Equal(fixture.Hash);
    }

    /// <summary>
    /// Every image in a pre-6.6 Prusa release in <c>firmware/</c> hashes to the content hash beside it,
    /// which is the one its signed firmware has compiled in.
    /// </summary>
    [Fact]
    public void EveryPrusaImageInTheFirmwareDirectoryHashesToItsEntry()
    {
        // Arrange
        string directory = Path.Combine(RepositoryRoot().FullName, "firmware");
        List<byte[]> releases = Directory.Exists(directory) ?
            [.. Directory.EnumerateFiles(directory, "*.bbf").Select(File.ReadAllBytes).Where(IsOlderLayout)] :
            [];

        Assert.SkipWhen(releases.Count == 0, $"no pre-6.6 Prusa .bbf in {directory}");

        foreach (byte[] release in releases)
        {
            Dictionary<byte, byte[]> entries = EntriesOf(release);

            foreach (byte image in (byte[])[1, 5])
            {
                // Act
                byte[]? hash = LittlefsImage.ContentHash(entries[image],
                                                         BinaryPrimitives.ReadUInt32LittleEndian(entries[(byte)(image + 1)]),
                                                         BinaryPrimitives.ReadUInt32LittleEndian(entries[(byte)(image + 2)]));

                // Assert
                hash.Should().Equal(entries[(byte)(image + 3)]);
            }
        }
    }

    /// <summary>A byte of a file changed: still an image, but of other files.</summary>
    [Fact]
    public void AChangedFileByteChangesTheHash()
    {
        // Arrange - into the middle of /fonts/a.bin, whose bytes are (k * 31 + 1) mod 256
        LittlefsFixture tree = LittlefsFixture.Tree;
        byte[] content = [.. Enumerable.Range(1500, 16).Select(k => (byte)((k * 31) + 1))];
        int at = tree.Image.AsSpan().IndexOf(content);
        at.Should().BePositive("the file's bytes are in the image");

        byte[] image = (byte[])tree.Image.Clone();
        image[at + 8] ^= 1;

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, tree.BlockSize, tree.BlockCount);

        // Assert
        hash.Should().NotBeNull().And.NotEqual(tree.Hash);
    }

    /// <summary>
    /// A byte of metadata changed fails the commit's CRC, and the commit with it. Changed in both blocks
    /// of the superblock pair, nothing is left to mount.
    /// </summary>
    [Fact]
    public void AChangedMetadataByteIsNotTheSameImage()
    {
        // Arrange - inside the first commit of the superblock pair
        LittlefsFixture tree = LittlefsFixture.Tree;
        byte[] image = (byte[])tree.Image.Clone();
        image[12] ^= 1;
        image[tree.BlockSize + 12] ^= 1;

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, tree.BlockSize, tree.BlockCount);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>
    /// The printer mounts with the block size and count beside the image; a superblock that says
    /// otherwise is refused, as littlefs refuses to mount it.
    /// </summary>
    [Theory]
    [InlineData(512u, 48u)]
    [InlineData(128u, 192u)]
    public void ABlockSizeTheSuperblockDoesNotNameIsRefused(uint blockSize, uint blockCount)
    {
        // Act
        byte[]? hash = LittlefsImage.ContentHash(LittlefsFixture.Tree.Image, blockSize, blockCount);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>
    /// The printer reads a block wherever block times size lands in the file, so an image that is not
    /// exactly its blocks would have it read past the image into whatever follows.
    /// </summary>
    [Fact]
    public void AnImageThatIsNotExactlyItsBlocksIsRefused()
    {
        // Arrange
        LittlefsFixture tree = LittlefsFixture.Tree;

        // Act
        byte[]? shorter = LittlefsImage.ContentHash(tree.Image, tree.BlockSize, tree.BlockCount - 1);
        byte[]? longer = LittlefsImage.ContentHash((byte[])[.. tree.Image, 0xff], tree.BlockSize, tree.BlockCount);

        // Assert
        shorter.Should().BeNull();
        longer.Should().BeNull();
    }

    /// <summary>
    /// The printer mounts with a 16-byte cache, which littlefs needs the block size a multiple of: an
    /// image littlefs itself reads fine with 200-byte blocks is one the printer cannot.
    /// </summary>
    [Fact]
    public void ABlockSizeThePrinterCannotMountIsRefused()
    {
        // Act
        byte[]? hash = LittlefsImage.ContentHash(LittlefsFixture.OddBlockSize, 200, 32);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>
    /// The printer's bbf cache is sized for 4096-byte blocks and halts the printer when the first one
    /// will not fit, so a larger block - which hashes the same files to the same hash - is refused.
    /// </summary>
    [Fact]
    public void ABlockLargerThanThePrintersCacheTakesIsRefused()
    {
        // Act
        byte[]? hash = LittlefsImage.ContentHash(LittlefsFixture.LargeBlock, 8192, 4);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>
    /// littlefs keeps a directory's names in its own order, and the printer hashes its copy in that
    /// order; a directory listing them in any other would hash differently there than here.
    /// </summary>
    [Fact]
    public void NamesOutOfLittlefsOrderAreRefused()
    {
        // Arrange - /many/f05 renamed z05, ahead of f06 in the listing
        byte[] image = LittlefsEdits.Replace(LittlefsFixture.Tree, "f05"u8, "z05"u8);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, LittlefsFixture.Tree.BlockSize, LittlefsFixture.Tree.BlockCount);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>A name edited the same way but kept in order reads: the refusal above is the order, not the edit.</summary>
    [Fact]
    public void ANameEditedInOrderStillReads()
    {
        // Arrange - /readme renamed readmf, still between /many and /web
        byte[] image = LittlefsEdits.Replace(LittlefsFixture.Tree, "readme"u8, "readmf"u8);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, LittlefsFixture.Tree.BlockSize, LittlefsFixture.Tree.BlockCount);

        // Assert
        hash.Should().NotBeNull().And.NotEqual(LittlefsFixture.Tree.Hash);
    }

    /// <summary>A superblock written for another geometry than the one the printer mounts with.</summary>
    [Fact]
    public void ASuperblockNamingAnotherBlockCountIsRefused()
    {
        // Arrange - version 2.1, 256-byte blocks, 96 of them, made 95
        byte[] image = LittlefsEdits.Replace(LittlefsFixture.Tree,
                                             [0x01, 0x00, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00],
                                             [0x01, 0x00, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x5f, 0x00, 0x00, 0x00]);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, LittlefsFixture.Tree.BlockSize, LittlefsFixture.Tree.BlockCount);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>A littlefs newer than the printer's 2.1 is refused, as its littlefs refuses to mount it.</summary>
    [Fact]
    public void ANewerLittlefsIsRefused()
    {
        // Arrange - version 2.1 made 3.0
        byte[] image = LittlefsEdits.Replace(LittlefsFixture.Tree,
                                             [0x01, 0x00, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00],
                                             [0x00, 0x00, 0x03, 0x00, 0x00, 0x01, 0x00, 0x00]);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, LittlefsFixture.Tree.BlockSize, LittlefsFixture.Tree.BlockCount);

        // Assert
        hash.Should().BeNull();
    }

    [Fact]
    public void AnotherMagicIsRefused()
    {
        // Arrange
        byte[] image = LittlefsEdits.Replace(LittlefsFixture.Tree, "littlefs"u8, "littlefz"u8);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(image, LittlefsFixture.Tree.BlockSize, LittlefsFixture.Tree.BlockCount);

        // Assert
        hash.Should().BeNull();
    }

    [Fact]
    public void AnErasedImageIsRefused()
    {
        // Arrange
        byte[] erased = new byte[256 * 16];
        Array.Fill(erased, (byte)0xff);

        // Act
        byte[]? hash = LittlefsImage.ContentHash(erased, 256, 16);

        // Assert
        hash.Should().BeNull();
    }

    /// <summary>
    /// littlefs takes any bytes in a name; a path made of them is not the one Prusa's tool hashes, so
    /// such a name is refused rather than guessed at.
    /// </summary>
    [Fact]
    public void ANameThatIsNotPlainAsciiIsRefused()
    {
        // Act
        byte[]? hash = LittlefsImage.ContentHash(LittlefsFixture.NonAsciiName, 256, 32);

        // Assert
        hash.Should().BeNull();
    }

    private static bool IsOlderLayout(byte[] release)
    {
        uint firmware = BinaryPrimitives.ReadUInt32LittleEndian(release.AsSpan(PrusaFirmwareVerifier.SignedFrom));

        return release.Length > PrusaFirmwareVerifier.FirmwareOffset + firmware &&
               release[PrusaFirmwareVerifier.FirmwareOffset + (int)firmware] == 1;
    }

    private static Dictionary<byte, byte[]> EntriesOf(byte[] release)
    {
        Dictionary<byte, byte[]> entries = [];
        int at = PrusaFirmwareVerifier.FirmwareOffset +
                 (int)BinaryPrimitives.ReadUInt32LittleEndian(release.AsSpan(PrusaFirmwareVerifier.SignedFrom));

        while (at < release.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(release.AsSpan(at + 1));
            entries[release[at]] = release[(at + 5)..(at + 5 + length)];
            at += 5 + length;
        }

        return entries;
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ??
               throw new InvalidOperationException($"No Homespool.slnx above {AppContext.BaseDirectory}.");
    }
}
