using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

using Homespool.Host.Firmware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaFirmwareVerifier"/> - a <c>.bbf</c> reaches a printer only intact and signed with
/// the verifier's key.
/// </summary>
/// <remarks>
/// <para>
/// The images are built here, in the layout <c>pack_fw.py</c> writes, and signed with a key made for
/// the run, because Prusa's are not ours to commit. That they are signed the way Prusa signs is what
/// <see cref="EveryPrusaImageInTheFirmwareDirectoryIsVerified"/> checks, against real releases.
/// </para>
/// </remarks>
public sealed class PrusaFirmwareVerifierTests
{
    private const int FirmwareLength = 1000;

    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");

    private static readonly AsymmetricCipherKeyPair Key = NewKey();

    private static readonly AsymmetricCipherKeyPair OtherKey = NewKey();

    private readonly PrusaFirmwareVerifier _verifier = new(PublicKeyOf(Key));

    [Fact]
    public async Task AnImageSignedWithTheKeyIsVerified()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(Image());

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
        PrusaFirmwareCheck check = await CheckAsync(Image(prerelease: "RC1"));

        // Assert
        check.Header!.Version.Should().Be("7.0.0-RC1+16903");
    }

    [Fact]
    public async Task AChangedFirmwareByteIsDamaged()
    {
        // Arrange
        byte[] image = Image();
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
        byte[] image = Image();
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
        byte[] image = Image();
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
        PrusaFirmwareCheck check = await CheckAsync(Image(signedWith: OtherKey));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.SignatureInvalid);
    }

    /// <summary>A local build carries zeros where the signature goes, and is told apart from a forgery.</summary>
    [Fact]
    public async Task AnImageWithNoSignatureSaysSo()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(Image(signed: false));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.NoSignature);
        check.Header!.Version.Should().Be("7.0.0+16903", "a refusal can still name what the file claimed to be");
    }

    [Fact]
    public async Task AnImageThatEndsInsideItsFirmwareIsTruncated()
    {
        // Arrange
        byte[] image = Image()[..(PrusaFirmwareVerifier.FirmwareOffset + (FirmwareLength / 2))];

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Truncated);
    }

    [Fact]
    public async Task AFileShorterThanAHeaderIsNotAnImage()
    {
        // Act
        PrusaFirmwareCheck check = await CheckAsync(Image()[..(PrusaFirmwareVerifier.FirmwareOffset - 1)]);

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
        PrusaFirmwareCheck check = await CheckAsync(Image(bbfVersion: 1));

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.NotAnImage);
    }

    /// <summary>
    /// The entries after the firmware are outside the signature, as Prusa packs them - the signed
    /// firmware checks them itself when it installs them. Pinned so a change here is a decision.
    /// </summary>
    [Fact]
    public async Task TheEntriesAfterTheFirmwareAreNotSigned()
    {
        // Arrange
        byte[] image = Image();
        image[^1] ^= 1;

        // Act
        PrusaFirmwareCheck check = await CheckAsync(image);

        // Assert
        check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified);
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
        byte[] key = PublicKeyOf(Key);
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
    /// is verified with Prusa's key - and stops being once one byte of its firmware changes, so a
    /// verifier that accepted anything would fail here too.
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

            // Act
            PrusaFirmwareCheck check = await CheckAsync(image, PrusaFirmwareVerifier.Prusa);
            PrusaFirmwareCheck tampered = await CheckAsync(changed, PrusaFirmwareVerifier.Prusa);

            // Assert
            check.Verdict.Should().Be(PrusaFirmwareVerdict.Verified, Path.GetFileName(path));
            tampered.IsVerified.Should().BeFalse(Path.GetFileName(path));
        }
    }

    private async Task<PrusaFirmwareCheck> CheckAsync(byte[] image, PrusaFirmwareVerifier? verifier = null)
    {
        await using MemoryStream stream = new(image, writable: false);

        return await (verifier ?? _verifier).CheckAsync(stream, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An image in <c>pack_fw.py</c>'s second layout: a Core One 7.0.0 header, a firmware of
    /// <see cref="FirmwareLength"/> bytes, and one trailing entry.
    /// </summary>
    /// <param name="prerelease">The prerelease label, or empty for a release.</param>
    /// <param name="bbfVersion">The header version to write.</param>
    /// <param name="signed">False to leave the signature as zeros, the way a local build does.</param>
    /// <param name="signedWith">The key to sign with, when signed; the run's own by default.</param>
    private static byte[] Image(string prerelease = "",
                                byte bbfVersion = PrusaFirmwareVerifier.BbfVersion,
                                bool signed = true,
                                AsymmetricCipherKeyPair? signedWith = null)
    {
        AsymmetricCipherKeyPair? key = signed ? signedWith ?? Key : null;

        byte[] body = new byte[PrusaFirmwareVerifier.FirmwareOffset - PrusaFirmwareVerifier.SignedFrom + FirmwareLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, FirmwareLength);
        body[4] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), 16903);
        Encoding.ASCII.GetBytes(prerelease).CopyTo(body, 9);
        body[15] = 7;
        body[16] = bbfVersion;
        body[18] = 1;

        for (int i = PrusaFirmwareVerifier.FirmwareOffset - PrusaFirmwareVerifier.SignedFrom; i < body.Length; i++)
        {
            body[i] = (byte)i;
        }

        byte[] digest = SHA256.HashData(body);
        byte[] signature = key is null ? new byte[PrusaFirmwareVerifier.SignatureLength] : Sign(digest, key);
        byte[] trailer = [9, 16, 0, 0, 0, .. Enumerable.Range(0, 16).Select(i => (byte)i)];

        return [.. signature, .. digest, .. body, .. trailer];
    }

    private static byte[] SignedRegion(byte[] image)
    {
        return image[PrusaFirmwareVerifier.SignedFrom..(PrusaFirmwareVerifier.FirmwareOffset + FirmwareLength)];
    }

    private static byte[] Sign(byte[] digest, AsymmetricCipherKeyPair key)
    {
        ECDsaSigner signer = new(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(forSigning: true, key.Private);

        BigInteger[] rs = signer.GenerateSignature(digest);

        return [.. BigIntegers.AsUnsignedByteArray(32, rs[0]), .. BigIntegers.AsUnsignedByteArray(32, rs[1])];
    }

    private static AsymmetricCipherKeyPair NewKey()
    {
        ECKeyPairGenerator generator = new();
        generator.Init(new ECKeyGenerationParameters(new ECDomainParameters(Curve), new SecureRandom()));

        return generator.GenerateKeyPair();
    }

    private static byte[] PublicKeyOf(AsymmetricCipherKeyPair key)
    {
        return ((ECPublicKeyParameters)key.Public).Q.GetEncoded(compressed: false)[1..];
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
