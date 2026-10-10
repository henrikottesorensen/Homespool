using System;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.Firmware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaTarball"/> - a tarball as a 6.6 or later printer unpacks one, and nothing else.
/// </summary>
/// <remarks>
/// Each refusal edits a well-formed tarball in one way and seals its header's checksum again, so it is
/// refused for that one thing rather than for a checksum the edit broke.
/// </remarks>
public sealed class PrusaTarballTests
{
    private static readonly byte[] Content = [.. Enumerable.Range(0, 700).Select(i => (byte)i)];

    [Fact]
    public void AFileWithItsContentAndTheClosingBlocksIsWellFormed()
    {
        PrusaTarball.IsWellFormed(TestFirmwareImages.Tarball("/fonts/a.bin", Content)).Should().BeTrue();
    }

    [Fact]
    public void ADirectoryThenAFileIsWellFormed()
    {
        // Arrange - a directory header, then the file's tarball, closing blocks and all
        byte[] directory = new byte[512];
        TestFirmwareImages.Tarball("/fonts/", [])[..512].CopyTo(directory, 0);
        directory[156] = (byte)'5';
        TestFirmwareImages.SealTarballHeader(directory);

        // Act, Assert
        PrusaTarball.IsWellFormed([.. directory, .. TestFirmwareImages.Tarball("/fonts/a.bin", Content)]).Should().BeTrue();
    }

    [Fact]
    public void NothingIsNotATarball()
    {
        PrusaTarball.IsWellFormed([]).Should().BeFalse();
    }

    [Fact]
    public void ALengthThatIsNotWholeBlocksIsRefused()
    {
        PrusaTarball.IsWellFormed([.. TestFirmwareImages.Tarball("/a.bin", Content), 0]).Should().BeFalse();
    }

    [Fact]
    public void OnlyClosingBlocksAreRefused()
    {
        PrusaTarball.IsWellFormed(new byte[1024]).Should().BeFalse("a tarball holds something");
    }

    [Fact]
    public void ABadChecksumIsRefused()
    {
        // Arrange - a name byte changed, the checksum left as it was
        byte[] tarball = TestFirmwareImages.Tarball("/a.bin", Content);
        tarball[2] = (byte)'b';

        // Act, Assert
        PrusaTarball.IsWellFormed(tarball).Should().BeFalse();
    }

    [Fact]
    public void AHeaderWithoutTheUstarMagicIsRefused()
    {
        PrusaTarball.IsWellFormed(Edited(header => Encoding.ASCII.GetBytes("gnutar").CopyTo(header, 257))).Should().BeFalse();
    }

    /// <summary>The extractor writes files and directories; a link or anything else it refuses, and so is it here.</summary>
    [Theory]
    [InlineData('1')]
    [InlineData('2')]
    [InlineData('x')]
    public void AMemberOtherThanAFileOrDirectoryIsRefused(char type)
    {
        // An empty file, so nothing but the closing blocks follows the header to be refused instead.
        PrusaTarball.IsWellFormed(Edited(header => header[156] = (byte)type, [])).Should().BeFalse();
    }

    /// <summary>The extractor's name rules: rooted, within the name field with no prefix, no <c>..</c>.</summary>
    [Theory]
    [InlineData("a.bin")]
    [InlineData("/../a.bin")]
    [InlineData("/fonts/../../a.bin")]
    public void ANameTheExtractorRefusesIsRefused(string name)
    {
        PrusaTarball.IsWellFormed(Edited(header =>
        {
            header.AsSpan(0, 100).Clear();
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        })).Should().BeFalse();
    }

    [Fact]
    public void ANameSpillingIntoThePrefixIsRefused()
    {
        PrusaTarball.IsWellFormed(Edited(header => Encoding.ASCII.GetBytes("fonts").CopyTo(header, 345))).Should().BeFalse();
    }

    [Fact]
    public void ASizeThatIsNotOctalIsRefused()
    {
        // An empty file: the digits before the 8 read as no content, so only the 8 is wrong.
        PrusaTarball.IsWellFormed(Edited(header => Encoding.ASCII.GetBytes("00000000008\0").CopyTo(header, 124), [])).Should().BeFalse();
    }

    [Fact]
    public void AFileLongerThanTheTarballIsRefused()
    {
        PrusaTarball.IsWellFormed(Edited(header => Encoding.ASCII.GetBytes("00000100000\0").CopyTo(header, 124))).Should().BeFalse();
    }

    /// <summary>
    /// A size near 2^31 bytes rounds past what an offset can hold; refused, not wrapped into a negative
    /// offset that would read the tarball from somewhere before its start.
    /// </summary>
    [Fact]
    public void AFileClaimingTwoGigabytesIsRefused()
    {
        PrusaTarball.IsWellFormed(Edited(header => Encoding.ASCII.GetBytes("17777777777\0").CopyTo(header, 124))).Should().BeFalse();
    }

    /// <summary>
    /// The extractor passes over a block that is not a header; here every block must be one, a file's
    /// content, or a closing block - stricter, and nothing Prusa builds is refused for it.
    /// </summary>
    [Fact]
    public void AnythingAfterTheClosingBlocksIsRefused()
    {
        PrusaTarball.IsWellFormed([.. TestFirmwareImages.Tarball("/a.bin", Content), .. Enumerable.Repeat((byte)1, 512)]).Should().BeFalse();
    }

    [Fact]
    public void ATarballWithoutClosingBlocksIsRefused()
    {
        byte[] tarball = TestFirmwareImages.Tarball("/a.bin", Content);

        PrusaTarball.IsWellFormed(tarball[..^1024]).Should().BeFalse();
    }

    /// <summary>A one-file tarball with its header edited by <paramref name="edit"/> and sealed again.</summary>
    private static byte[] Edited(Action<byte[]> edit, byte[]? content = null)
    {
        byte[] tarball = TestFirmwareImages.Tarball("/a.bin", content ?? Content);
        byte[] header = tarball[..512];
        edit(header);
        TestFirmwareImages.SealTarballHeader(header);
        header.CopyTo(tarball, 0);

        return tarball;
    }
}
