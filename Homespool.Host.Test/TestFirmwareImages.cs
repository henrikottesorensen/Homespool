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

    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");

    /// <summary>The key images are signed with unless a test says otherwise.</summary>
    public static AsymmetricCipherKeyPair Key { get; } = NewKey();

    /// <summary>A key that is not <see cref="Key"/>, for an image signed by somebody else.</summary>
    public static AsymmetricCipherKeyPair OtherKey { get; } = NewKey();

    /// <summary>The verifier holding <see cref="Key"/>.</summary>
    public static PrusaFirmwareVerifier Verifier { get; } = new(PublicKeyOf(Key));

    /// <summary>
    /// An image: a Core One 7.0.0 build unless told otherwise, <see cref="FirmwareLength"/> bytes of
    /// firmware, and one trailing entry.
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
    public static byte[] Build(string prerelease = "",
                               byte bbfVersion = PrusaFirmwareVerifier.BbfVersion,
                               bool signed = true,
                               AsymmetricCipherKeyPair? signedWith = null,
                               byte printerType = 7,
                               byte printerVersion = 1,
                               byte printerSubversion = 0,
                               ushort build = 16903,
                               byte seed = 0)
    {
        AsymmetricCipherKeyPair? key = signed ? signedWith ?? Key : null;
        const int headerLength = PrusaFirmwareVerifier.FirmwareOffset - PrusaFirmwareVerifier.SignedFrom;

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

        byte[] digest = SHA256.HashData(body);
        byte[] signature = key is null ? new byte[PrusaFirmwareVerifier.SignatureLength] : Sign(digest, key);
        byte[] trailer = [9, 16, 0, 0, 0, .. Enumerable.Range(0, 16).Select(i => (byte)i)];

        return [.. signature, .. digest, .. body, .. trailer];
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
