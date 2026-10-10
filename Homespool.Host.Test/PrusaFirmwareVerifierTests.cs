using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Firmware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaFirmwareVerifier"/> - a <c>.bbf</c> reaches a printer only intact and signed with
/// the verifier's key.
/// </summary>
/// <remarks>
/// <para>
/// The images come from <see cref="TestFirmwareImages"/>, signed with a key made for the run, because
/// Prusa's are not ours to commit. That they are signed the way Prusa signs is what
/// <see cref="PrusasKeySignsTheCoreOne700Release"/> and
/// <see cref="EveryPrusaImageInTheFirmwareDirectoryIsVerified"/> check, against real releases.
/// </para>
/// </remarks>
public sealed class PrusaFirmwareVerifierTests
{
    private const int FirmwareLength = TestFirmwareImages.FirmwareLength;

    /// <summary>Where the entries after the firmware begin in a built image.</summary>
    private const int EntriesFrom = PrusaFirmwareVerifier.FirmwareOffset + FirmwareLength;

    /// <summary>An entry's type and length, before its content.</summary>
    private const int EntryHeader = 5;

    private const int DigestLength = PrusaFirmwareVerifier.DigestLength;

    private readonly PrusaFirmwareVerifier _verifier = TestFirmwareImages.Verifier;

    /// <summary>Which tarball a test changes.</summary>
    public enum TarballChange
    {
        /// <summary>Never set.</summary>
        Undefined = 0,

        /// <summary>The resources tarball, the first entry.</summary>
        Resources = 1,

        /// <summary>The bootloader tarball, the third.</summary>
        Bootloader = 2,
    }

    [Fact]
    public async Task AnImageSignedWithTheKeyIsVerified()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build());

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
        check.Header.Should().Be(new PrusaFirmwareHeader(Major: 7,
                                                         Minor: 0,
                                                         Patch: 0,
                                                         Build: 16903,
                                                         Prerelease: string.Empty,
                                                         Board: 0,
                                                         PrinterType: 7,
                                                         PrinterVersion: 1,
                                                         PrinterSubversion: 0,
                                                         FirmwareLength: FirmwareLength));
        check.Header!.Version.Should().Be("7.0.0+16903");
    }

    [Fact]
    public async Task APrereleaseCarriesItsLabelInTheVersion()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(prerelease: "RC1"));

        // Assert
        check.Header!.Version.Should().Be("7.0.0-RC1+16903");
    }

    [Fact]
    public async Task AChangedFirmwareByteIsDamaged()
    {
        // Arrange
        byte[] image = TestFirmwareImages.Build();
        image[PrusaFirmwareVerifier.FirmwareOffset + 500] ^= 1;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Damaged);
    }

    /// <summary>
    /// The header is inside the signed region, so a file cannot claim another printer or version.
    /// </summary>
    [Fact]
    public async Task AChangedHeaderByteIsDamaged()
    {
        // Arrange - the printer type, 7 for a Core One, made 1 for the MK4 family
        byte[] image = TestFirmwareImages.Build();
        image[PrusaFirmwareVerifier.SignedFrom + 15] = 1;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Damaged);
    }

    /// <summary>
    /// A changed image with its digest written to match is intact as far as the digest knows - and
    /// still refused, because the digest is not what vouches for it.
    /// </summary>
    [Fact]
    public async Task AChangedImageWithAFreshDigestIsRefused()
    {
        // Arrange
        byte[] image = TestFirmwareImages.Build();
        image[PrusaFirmwareVerifier.FirmwareOffset + 500] ^= 1;
        SHA256.HashData(SignedRegion(image)).CopyTo(image, PrusaFirmwareVerifier.SignatureLength);

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.SignatureInvalid);
    }

    [Fact]
    public async Task AnImageSignedWithAnotherKeyIsRefused()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(signedWith: TestFirmwareImages.OtherKey));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.SignatureInvalid);
    }

    /// <summary>A local build carries zeros where the signature goes, and is told apart from a forgery.</summary>
    [Fact]
    public async Task AnImageWithNoSignatureSaysSo()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(signed: false));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.NoSignature);
        check.Header!.Version.Should().Be("7.0.0+16903", "a refusal can still name what the file claimed to be");
    }

    [Fact]
    public async Task AnImageThatEndsInsideItsFirmwareIsTruncated()
    {
        // Arrange
        byte[] image = TestFirmwareImages.Build()[..(PrusaFirmwareVerifier.FirmwareOffset + (FirmwareLength / 2))];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Truncated);
    }

    [Fact]
    public async Task AFileShorterThanAHeaderIsNotAnImage()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build()[..(PrusaFirmwareVerifier.FirmwareOffset - 1)]);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.NotAnImage);
        check.Header.Should().BeNull();
    }

    /// <summary>
    /// The first header version puts printer version and subversion where the second has the header
    /// version, so reading one as the other would misname the printer it is for.
    /// </summary>
    [Fact]
    public async Task AHeaderOfAnotherVersionIsNotAnImage()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(bbfVersion: 1));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.NotAnImage);
    }

    [Fact]
    public async Task AVerifiedImageIsNamedByTheDigestItsSignatureCovers()
    {
        // Arrange
        byte[] image = TestFirmwareImages.Build();

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.SignedDigest.Should().Be(Base64Url.EncodeToString(SHA256.HashData(SignedRegion(image))));
    }

    [Fact]
    public async Task ARefusedImageIsNotNamed()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(signed: false));

        // Assert
        check.SignedDigest.Should().BeNull();
    }

    /// <summary>
    /// Each tarball after the firmware is outside the signature, so a changed byte in either is caught
    /// by its digest entry.
    /// </summary>
    [Theory]
    [InlineData(TarballChange.Resources)]
    [InlineData(TarballChange.Bootloader)]
    public async Task AChangedTarballByteIsRefused(TarballChange change)
    {
        // Arrange
        byte[] image = TestFirmwareImages.Build();
        int at = change == TarballChange.Resources ?
            EntriesFrom + EntryHeader :
            EntriesFrom + (3 * EntryHeader) + TestFirmwareImages.ResourcesTarball.Length + DigestLength;
        image[at + 3] ^= 1;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
        check.Header!.Version.Should().Be("7.0.0+16903", "a refusal can still name what the file claimed to be");
    }

    /// <summary>
    /// The reviewer's case: another tarball with a digest entry written to match. Only the signed
    /// firmware's own copy of the digest refuses it.
    /// </summary>
    [Theory]
    [InlineData(TarballChange.Resources)]
    [InlineData(TarballChange.Bootloader)]
    public async Task AnotherTarballWithAMatchingDigestEntryIsRefused(TarballChange change)
    {
        // Arrange
        byte[] other = TestFirmwareImages.Tarball("/other.bin", [.. Enumerable.Range(0, 64).Select(i => (byte)(255 - i))]);
        byte[] entries = change == TarballChange.Resources ?
            TestFirmwareImages.Entries(other, TestFirmwareImages.BootloaderTarball) :
            TestFirmwareImages.Entries(TestFirmwareImages.ResourcesTarball, other);

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: entries));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>
    /// The review's case: the bootloader tarball twice. Each copy hashes to its digest, and that digest
    /// is in the signed bytes - but a printer needing new resources finds no file naming them, and waits
    /// in bootstrap for ever.
    /// </summary>
    [Fact]
    public async Task OneTarballTwiceIsRefused()
    {
        // Arrange
        byte[] twice = TestFirmwareImages.Entries(TestFirmwareImages.BootloaderTarball, TestFirmwareImages.BootloaderTarball);

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: twice));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>
    /// The 10-09 review's case: a pre-6.6 firmware names its littlefs images' content hashes, each a
    /// SHA-256 over a stream anyone can rebuild - marks, paths and file bytes - so those streams would
    /// pass as tarballs whose digests the firmware names. They are not tarballs, and are refused.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AContentHashStreamPassedAsATarballIsRefused(bool padded)
    {
        // Arrange - streams shaped as mklittlefs.py hashes: a counter, a path, a counter, file bytes...
        byte[] Stream(string path, int length)
        {
            byte[] stream = [0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes(path), 1, 0, 0, 0, .. Enumerable.Range(0, length).Select(i => (byte)i)];

            return padded ? [.. stream, .. new byte[(512 - (stream.Length % 512)) % 512]] : stream;
        }

        byte[] resources = Stream("/", 300);
        byte[] bootloader = Stream("/bootloader.bin", 200);

        // Act - a firmware naming the streams' digests, as a pre-6.6 one names its content hashes
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: TestFirmwareImages.Entries(resources, bootloader),
                                                                             named: (SHA256.HashData(resources), SHA256.HashData(bootloader))));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    /// <summary>
    /// The printer looks both tarballs up by value, trying each digest entry for each revision, so the
    /// two in each other's places still install - and are accepted, as the same image.
    /// </summary>
    [Fact]
    public async Task TheTwoTarballsInEachOthersPlacesAreTheSameImage()
    {
        // Arrange
        byte[] swapped = TestFirmwareImages.Entries(TestFirmwareImages.BootloaderTarball, TestFirmwareImages.ResourcesTarball);

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: swapped));
        PrusaFirmwareCheck original = await CheckAsync(TestFirmwareImages.Build());

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
        check.SignedDigest.Should().Be(original.SignedDigest);
    }

    /// <summary>
    /// A digest is found wherever it lies in the signed region, across the seam between the header and
    /// the firmware - which the verifier reads as two pieces - included.
    /// </summary>
    [Fact]
    public async Task ADigestAcrossTheHeadersEndIsFound()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(resourcesDigestAt: TestFirmwareImages.HeaderLength - 16));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
    }

    /// <summary>A digest across the seam between two of the firmware's read buffers is found too.</summary>
    [Fact]
    public async Task ADigestAcrossTwoReadsIsFound()
    {
        // Arrange - a stream that hands out ten bytes a read, so every digest straddles reads
        byte[] image = TestFirmwareImages.Build();
        await using TrickleStream stream = new(image, 10);

        // Act
        PrusaFirmwareCheck check = await _verifier.CheckAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
    }

    [Fact]
    public async Task BytesAfterTheLastEntryAreRefused()
    {
        // Arrange
        byte[] image = [.. TestFirmwareImages.Build(), 0];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    [Fact]
    public async Task AnUnknownEntryAfterTheLastIsRefused()
    {
        // Arrange
        byte[] image = [.. TestFirmwareImages.Build(), .. TestFirmwareImages.Entry(13, [1, 2, 3])];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    /// <summary>
    /// Without its bootloader tarball, a printer that needs the bootloader updated would wait for ever
    /// for a file carrying one.
    /// </summary>
    [Fact]
    public async Task AnImageMissingATarballIsRefused()
    {
        // Arrange
        byte[] resourcesOnly = [.. TestFirmwareImages.Entry(9, TestFirmwareImages.ResourcesTarball),
                                .. TestFirmwareImages.Entry(10, SHA256.HashData(TestFirmwareImages.ResourcesTarball))];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: resourcesOnly));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    [Fact]
    public async Task AnImageWithNothingAfterTheFirmwareIsRefused()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: []));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    /// <summary>
    /// A release from before 6.6: two littlefs images, each with its block size, count and content
    /// hash, and the firmware naming both hashes. Verified, and named by its signed digest like any other.
    /// </summary>
    [Fact]
    public async Task AnOlderReleaseWhoseImagesHashToWhatItsFirmwareNamesIsVerified()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(OlderRelease());

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
        check.SignedDigest.Should().NotBeNull();
    }

    /// <summary>
    /// A byte of a file in an image changed: the files the printer would copy out are not the ones its
    /// firmware names, and it would retry them for ever.
    /// </summary>
    [Fact]
    public async Task AnOlderReleaseWithAFileChangedIsRefused()
    {
        // Arrange - into /fonts/a.bin in the resources image
        LittlefsFixture tree = LittlefsFixture.Tree;
        byte[] image = (byte[])tree.Image.Clone();
        image[image.AsSpan().IndexOf([.. Enumerable.Range(1500, 16).Select(k => (byte)((k * 31) + 1))]) + 8] ^= 1;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(OlderRelease(resources: tree with { Image = image }));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>
    /// The block count beside an image is unsigned, and is what the printer mounts it with; one that
    /// is not the image's is refused, as littlefs would refuse to mount it.
    /// </summary>
    [Fact]
    public async Task AnOlderReleaseWithAnotherBlockCountIsRefused()
    {
        // Arrange
        LittlefsFixture tree = LittlefsFixture.Tree;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(OlderRelease(resources: tree with { BlockCount = tree.BlockCount - 1 }));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>
    /// The reviewer's case in the older layout: another image, with a content hash entry written to
    /// match it. Only the signed firmware's own copy of the hash refuses it.
    /// </summary>
    [Fact]
    public async Task AnOlderReleaseWithAnotherImageAndItsHashIsRefused()
    {
        // Arrange - the firmware names the tree for both; the bootloader entries carry another image
        LittlefsFixture tree = LittlefsFixture.Tree;
        byte[] entries = [.. ImageEntriesOf(1, tree), .. ImageEntriesOf(5, LittlefsFixture.Rewritten)];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: entries, named: (tree.Hash, tree.Hash)));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>The review's case in the older layout: the bootloader image twice.</summary>
    [Fact]
    public async Task AnOlderReleaseWithOneImageTwiceIsRefused()
    {
        // Arrange - the firmware names both images; the file carries the bootloader's in both places
        LittlefsFixture resources = LittlefsFixture.Tree;
        LittlefsFixture bootloader = LittlefsFixture.Rewritten;
        byte[] entries = [.. ImageEntriesOf(1, bootloader), .. ImageEntriesOf(5, bootloader)];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: entries, named: (resources.Hash, bootloader.Hash)));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged);
    }

    /// <summary>An older release without its bootloader image is not laid out as one.</summary>
    [Fact]
    public async Task AnOlderReleaseMissingAnImageIsRefused()
    {
        // Arrange
        LittlefsFixture tree = LittlefsFixture.Tree;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: ImageEntriesOf(1, tree),
                                                                             named: (tree.Hash, tree.Hash)));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    [Fact]
    public async Task AnOlderReleaseThatEndsInsideAnImageIsTruncated()
    {
        // Arrange
        byte[] release = OlderRelease();

        // Act
        PrusaFirmwareCheck check = await CheckAsync(release[..(EntriesFrom + EntryHeader + 1000)]);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Truncated);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public async Task ADigestEntryOfAnotherLengthIsRefused(int length)
    {
        // Arrange - the right digest, cut short or with a byte after it
        byte[] digest = [.. SHA256.HashData(TestFirmwareImages.ResourcesTarball), 0];
        byte[] entries = [.. TestFirmwareImages.Entry(9, TestFirmwareImages.ResourcesTarball),
                          .. TestFirmwareImages.Entry(10, digest[..length]),
                          .. TestFirmwareImages.Entry(11, TestFirmwareImages.BootloaderTarball),
                          .. TestFirmwareImages.Entry(12, SHA256.HashData(TestFirmwareImages.BootloaderTarball))];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: entries));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    /// <summary>
    /// The printer finds each entry by its type, so the bootloader's digest under any other type is a
    /// digest it never finds - however right its length and its bytes.
    /// </summary>
    [Fact]
    public async Task AnEntryOfAnotherTypeIsRefused()
    {
        // Arrange
        byte[] entries = TestFirmwareImages.Entries(TestFirmwareImages.ResourcesTarball, TestFirmwareImages.BootloaderTarball);
        entries[^(EntryHeader + DigestLength)] = 13;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: entries));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    [Fact]
    public async Task AnEmptyTarballIsRefused()
    {
        // Arrange
        byte[] empty = [.. TestFirmwareImages.Entry(9, []),
                        .. TestFirmwareImages.Entry(10, SHA256.HashData([])),
                        .. TestFirmwareImages.Entry(11, TestFirmwareImages.BootloaderTarball),
                        .. TestFirmwareImages.Entry(12, SHA256.HashData(TestFirmwareImages.BootloaderTarball))];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(entries: empty));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesUnreadable);
    }

    [Theory]
    [InlineData("an entry's type and length")]
    [InlineData("a tarball")]
    [InlineData("a digest")]
    public async Task AnImageThatEndsInsideItsEntriesIsTruncated(string inside)
    {
        // Arrange
        int keep = inside switch
        {
            "an entry's type and length" => 3,
            "a tarball" => EntryHeader + 10,
            _ => EntryHeader + TestFirmwareImages.ResourcesTarball.Length + EntryHeader + 10,
        };
        byte[] image = TestFirmwareImages.Build()[..(EntriesFrom + keep)];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Truncated);
    }

    /// <summary>
    /// The entries are checked after the signature, so an image that is not Prusa's is called that,
    /// whatever follows its firmware.
    /// </summary>
    [Fact]
    public async Task AnImageSignedWithAnotherKeyIsRefusedAsThatWhateverItsEntries()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(TestFirmwareImages.Build(signedWith: TestFirmwareImages.OtherKey, entries: []));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.SignatureInvalid);
    }

    /// <summary>
    /// Nothing after the firmware is read until the signature holds: a file that is not Prusa's has
    /// its tail - a littlefs image, in an older release - never parsed at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NothingAfterTheFirmwareIsReadBeforeTheSignatureHolds(bool older)
    {
        // Arrange - signed with another key
        byte[] entries = older ?
            [.. TestFirmwareImages.ImageEntries(1, LittlefsFixture.Tree.Image, 256, 96, LittlefsFixture.Tree.Hash),
             .. TestFirmwareImages.ImageEntries(5, LittlefsFixture.Rewritten.Image, 256, 64, LittlefsFixture.Rewritten.Hash)] :
            TestFirmwareImages.Entries(TestFirmwareImages.ResourcesTarball, TestFirmwareImages.BootloaderTarball);
        byte[] image = TestFirmwareImages.Build(signedWith: TestFirmwareImages.OtherKey, entries: entries);
        await using TrickleStream stream = new(image, 4096);

        // Act
        PrusaFirmwareCheck check = await _verifier.CheckAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.SignatureInvalid);
        stream.Furthest.Should().BeLessThanOrEqualTo(EntriesFrom, "the entries begin where the firmware ends");
    }

    [Fact]
    public async Task AStreamThatCannotSeekIsRefused()
    {
        // Arrange
        await using TrickleStream stream = new(TestFirmwareImages.Build(), 10, canSeek: false);

        // Act
        Func<Task> check = () => _verifier.CheckAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        await check.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void AKeyOfTheWrongLengthIsRefused(int length)
    {
        // Act
        Action create = () => _ = new PrusaFirmwareVerifier(new byte[length]);

        // Assert
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AKeyOffTheCurveIsRefused()
    {
        // Arrange - a valid x with y moved by one, which no point on the curve has
        byte[] key = TestFirmwareImages.PublicKeyOf(TestFirmwareImages.Key);
        key[^1] ^= 1;

        // Act
        Action create = () => _ = new PrusaFirmwareVerifier(key);

        // Assert
        create.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Prusa's key, checked against a real release wherever the test runs: the signature and digest from
    /// the first 96 bytes of the Core One 7.0.0 image. A signature and a hash are not the release, so
    /// they can live here where the release cannot.
    /// </summary>
    [Fact]
    public void PrusasKeySignsTheCoreOne700Release()
    {
        // Arrange
        byte[] signature = Convert.FromHexString(
            "b3928ba2bd90b7194ace18257f066a6c1873018b55017f6c0e7f6acd5f3253a4" +
            "6d788b6e8cc7c5a1e729832c6ee2352764c9d2be2296abfc0a5e8a3ee5124453");
        byte[] digest = Convert.FromHexString("0bc81c1c1c4276eeeb273c71c4e160e7f3d59b17501a2a5fe1c2938ceb1f58ca");

        byte[] otherDigest = (byte[])digest.Clone();
        otherDigest[0] ^= 1;

        // Act
        bool signs = PrusaFirmwareVerifier.Prusa.Signs(signature, digest);
        bool signsOther = PrusaFirmwareVerifier.Prusa.Signs(signature, otherDigest);

        // Assert
        signs.Should().BeTrue();
        signsOther.Should().BeFalse();
    }

    /// <summary>
    /// Every official image in the repository's <c>firmware/</c> directory, which is never committed,
    /// is verified with Prusa's key and named by its own signed digest - and stops being once one byte
    /// of its firmware, or of its last tarball, changes, so a verifier that accepted anything would fail
    /// here too.
    /// </summary>
    [Fact]
    public async Task EveryPrusaImageInTheFirmwareDirectoryIsVerified()
    {
        // Arrange
        string directory = Path.Combine(RepositoryRoot().FullName, "firmware");
        List<string> images = Directory.Exists(directory) ?
            [.. Directory.EnumerateFiles(directory, "*.bbf")] :
            [];

        Assert.SkipWhen(images.Count == 0, $"no Prusa .bbf in {directory}");

        foreach (string path in images)
        {
            byte[] image = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            byte[] changed = (byte[])image.Clone();
            changed[PrusaFirmwareVerifier.FirmwareOffset + 500] ^= 1;

            // The last entry is the bootloader's digest or content hash, which the signed firmware names.
            byte[] changedTail = (byte[])image.Clone();
            changedTail[^1] ^= 1;

            // Act
            PrusaFirmwareCheck check = await CheckAsync(image, PrusaFirmwareVerifier.Prusa);
            PrusaFirmwareCheck tampered = await CheckAsync(changed, PrusaFirmwareVerifier.Prusa);
            PrusaFirmwareCheck tamperedTail = await CheckAsync(changedTail, PrusaFirmwareVerifier.Prusa);

            // Assert
            check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified, Path.GetFileName(path));
            check.SignedDigest.Should().Be(Base64Url.EncodeToString(image[PrusaFirmwareVerifier.SignatureLength..PrusaFirmwareVerifier.SignedFrom]),
                                           Path.GetFileName(path));
            tampered.IsVerified.Should().BeFalse(Path.GetFileName(path));
            tamperedTail.Verdict.Should().Be(PrusaFirmwareVerdict.ResourcesChanged, Path.GetFileName(path));
        }
    }

    private async Task<PrusaFirmwareCheck> CheckAsync(byte[] image, PrusaFirmwareVerifier? verifier = null)
    {
        await using MemoryStream stream = new(image, writable: false);

        return await (verifier ?? _verifier).CheckAsync(stream, TestContext.Current.CancellationToken);
    }

    private static byte[] SignedRegion(byte[] image)
    {
        return image[PrusaFirmwareVerifier.SignedFrom..(PrusaFirmwareVerifier.FirmwareOffset + FirmwareLength)];
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

    /// <summary>
    /// A release laid out as before 6.6, its firmware naming the two images' content hashes: the tree
    /// fixture for the resources and the rewritten one for the bootloader unless told otherwise.
    /// </summary>
    private static byte[] OlderRelease(LittlefsFixture? resources = null, LittlefsFixture? bootloader = null)
    {
        resources ??= LittlefsFixture.Tree;
        bootloader ??= LittlefsFixture.Rewritten;

        return TestFirmwareImages.Build(entries: [.. ImageEntriesOf(1, resources), .. ImageEntriesOf(5, bootloader)],
                                        named: (resources.Hash, bootloader.Hash));
    }

    private static byte[] ImageEntriesOf(byte firstType, LittlefsFixture fixture)
    {
        return TestFirmwareImages.ImageEntries(firstType, fixture.Image, fixture.BlockSize, fixture.BlockCount, fixture.Hash);
    }

    /// <summary>A stream over bytes that hands out at most a few of them a read.</summary>
    private sealed class TrickleStream(byte[] bytes, int perRead, bool canSeek = true) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => canSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <summary>The furthest byte any read has reached.</summary>
        public long Furthest { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, Math.Min(count, perRead));
            Furthest = Math.Max(Furthest, _inner.Position);

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return canSeek ? _inner.Seek(offset, origin) : throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
