using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
/// Firmware images in the layout <c>pack_fw.py</c> writes, signed with a key made for the test run,
/// and the verifier that holds that key.
/// </summary>
/// <remarks>
/// Prusa's images are not ours to commit, so tests build their own and verify them with
/// <see cref="Verifier"/> rather than <see cref="PrusaFirmwareVerifier.Prusa"/>. That Prusa's real
/// images verify the same way is the verifier's own tests' business.
/// </remarks>
internal static class TestFirmwareImages
{
    /// <summary>The firmware length every built image has.</summary>
    public const int FirmwareLength = 1000;

    /// <summary>
    /// Where the bootloader tarball's digest is written, counted from the start of the signed region:
    /// inside the firmware, and before the resources one, as some builds lay them out.
    /// </summary>
    public const int BootloaderDigestAt = HeaderLength + 200;

    /// <summary>Where the resources tarball's digest is written, by default; apart from the other.</summary>
    public const int ResourcesDigestAt = HeaderLength + 300;

    /// <summary>The header's length: the signed region's first bytes.</summary>
    public const int HeaderLength = PrusaFirmwareVerifier.FirmwareOffset - PrusaFirmwareVerifier.SignedFrom;

    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");

    /// <summary>The key images are signed with unless a test says otherwise.</summary>
    public static AsymmetricCipherKeyPair Key { get; } = NewKey();

    /// <summary>A key that is not <see cref="Key"/>, for an image signed by somebody else.</summary>
    public static AsymmetricCipherKeyPair OtherKey { get; } = NewKey();

    /// <summary>The verifier holding <see cref="Key"/>.</summary>
    public static PrusaFirmwareVerifier Verifier { get; } = new(PublicKeyOf(Key));

    /// <summary>The resources tarball every built image carries.</summary>
    public static byte[] ResourcesTarball { get; } = [.. Enumerable.Range(0, 64).Select(i => (byte)(i + 0x10))];

    /// <summary>The bootloader tarball every built image carries.</summary>
    public static byte[] BootloaderTarball { get; } = [.. Enumerable.Range(0, 48).Select(i => (byte)(i + 0x80))];

    /// <summary>
    /// An image: a Core One 7.0.0 build unless told otherwise, <see cref="FirmwareLength"/> bytes of
    /// firmware naming <see cref="ResourcesTarball"/> and <see cref="BootloaderTarball"/> by their
    /// digests, and then the entries <c>pack_fw.py</c> writes for them.
    /// </summary>
    /// <param name="prerelease">The prerelease label, or empty for a release.</param>
    /// <param name="bbfVersion">The header version to write.</param>
    /// <param name="signed">False to leave the signature as zeros, the way a local build does.</param>
    /// <param name="signedWith">The key to sign with, when signed; <see cref="Key"/> by default.</param>
    /// <param name="printerType">The printer family; 7 is the Core One.</param>
    /// <param name="printerVersion">The version within the family.</param>
    /// <param name="printerSubversion">The subversion within the family.</param>
    /// <param name="build">The build number.</param>
    /// <param name="seed">Varies the firmware's bytes, so two images can differ in nothing else.</param>
    /// <param name="resourcesDigestAt">
    /// Where in the signed region the resources tarball's digest is written; <see cref="ResourcesDigestAt"/>
    /// by default.
    /// </param>
    /// <param name="entries">What follows the firmware, in place of <see cref="Entries"/>.</param>
    /// <param name="named">
    /// The resources' and the bootloader's digests to compile into the firmware, in place of the two
    /// tarballs' - an older release names its images' content hashes.
    /// </param>
    public static byte[] Build(string prerelease = "",
                               byte bbfVersion = PrusaFirmwareVerifier.BbfVersion,
                               bool signed = true,
                               AsymmetricCipherKeyPair? signedWith = null,
                               byte printerType = 7,
                               byte printerVersion = 1,
                               byte printerSubversion = 0,
                               ushort build = 16903,
                               byte seed = 0,
                               int resourcesDigestAt = ResourcesDigestAt,
                               byte[]? entries = null,
                               (byte[] resources, byte[] bootloader)? named = null)
    {
        AsymmetricCipherKeyPair? key = signed ? signedWith ?? Key : null;
        const int headerLength = HeaderLength;

        byte[] body = new byte[headerLength + FirmwareLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, FirmwareLength);
        body[4] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), build);
        Encoding.ASCII.GetBytes(prerelease).CopyTo(body, 9);
        body[15] = printerType;
        body[16] = bbfVersion;
        body[17] = printerSubversion;
        body[18] = printerVersion;

        for (int i = headerLength; i < body.Length; i++)
        {
            body[i] = (byte)(i + seed);
        }

        // What the build does with objcopy: each tarball's digest compiled into the firmware.
        (named?.bootloader ?? SHA256.HashData(BootloaderTarball)).CopyTo(body, BootloaderDigestAt);
        (named?.resources ?? SHA256.HashData(ResourcesTarball)).CopyTo(body, resourcesDigestAt);

        byte[] digest = SHA256.HashData(body);
        byte[] signature = key is null ? new byte[PrusaFirmwareVerifier.SignatureLength] : Sign(digest, key);

        return [.. signature, .. digest, .. body, .. entries ?? Entries(ResourcesTarball, BootloaderTarball)];
    }

    /// <summary>
    /// The four entries <c>pack_fw.py</c> writes after the firmware: each tarball, then its digest.
    /// </summary>
    public static byte[] Entries(byte[] resources, byte[] bootloader)
    {
        return [.. Entry(9, resources), .. Entry(10, SHA256.HashData(resources)),
                .. Entry(11, bootloader), .. Entry(12, SHA256.HashData(bootloader))];
    }

    /// <summary>
    /// The four entries an older release writes for one littlefs image, from <paramref name="firstType"/>:
    /// the image, its block size, its block count and its content hash.
    /// </summary>
    public static byte[] ImageEntries(byte firstType, byte[] image, uint blockSize, uint blockCount, byte[] contentHash)
    {
        byte[] size = new byte[4];
        byte[] count = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(count, blockCount);

        return [.. Entry(firstType, image), .. Entry((byte)(firstType + 1), size),
                .. Entry((byte)(firstType + 2), count), .. Entry((byte)(firstType + 3), contentHash)];
    }

    /// <summary>One entry: its type, its length as a little-endian 32-bit number, its content.</summary>
    public static byte[] Entry(byte type, byte[] content)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)content.Length);

        return [type, .. length, .. content];
    }

    /// <summary>
    /// <paramref name="image"/> with its signature's <c>s</c> replaced by <c>n - s</c>: a different
    /// signature over the same digest, which verifies all the same.
    /// </summary>
    public static byte[] WithOtherSignature(byte[] image)
    {
        byte[] other = (byte[])image.Clone();
        BigInteger s = new(1, image[32..64]);
        BigIntegers.AsUnsignedByteArray(32, Curve.N.Subtract(s)).CopyTo(other, 32);

        return other;
    }

    /// <summary>A key's public half, x then y, big-endian - the form a verifier takes.</summary>
    public static byte[] PublicKeyOf(AsymmetricCipherKeyPair key)
    {
        return ((ECPublicKeyParameters)key.Public).Q.GetEncoded(compressed: false)[1..];
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
}
