using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Test;

/// <summary>
/// Checks <see cref="TransferCipher"/> against the AES-CTR keystream Buddy's own transfer decryptor
/// produces - the server-to-printer side of the encrypted download, where a wrong keystream means a
/// file that transfers cleanly and prints as garbage.
/// </summary>
/// <remarks>
/// <para>
/// <c>keystream-fixtures.json</c> is <b>generated, not hand-written</b>, by a
/// <c>connect_keystream_dump</c> target that runs the real <c>transfers::Decryptor</c>
/// (<c>src/transfers/decrypt.cpp</c>) over zero-filled input. Decrypting zeroes returns the raw
/// keystream, because CTR XORs - so the fixture isolates the counter arithmetic with no plaintext,
/// no file and no HTTP in the way. Generated from the pinned upstream ref <c>e96ce2b92</c> (v6.6.0);
/// to regenerate, <c>./rig/rig.sh run connect_keystream_dump &gt; Homespool.Host.Test/keystream-fixtures.json</c>.
/// Nothing here reads the firmware checkout at test time - it is a machine-local path, and a fixture
/// that silently vanishes on another machine is worse than one that is committed.
/// </para>
/// <para>
/// <b>The first vector reaches past firmware to Connect itself.</b> Its key and IV are the ones in
/// Prusa's own <c>tests/unit/transfers/decrypt.cpp</c>, which decrypts a <c>box.crypt</c> /
/// <c>box.gcode</c> pair their server produced. That pair's XOR equals this vector's keystream
/// exactly (checked when the fixture was generated), so agreeing with it means agreeing with
/// ciphertext neither side of this project made. The remaining vectors are ours and exist for the
/// offsets: the block index crossing each byte boundary, the tail start of the 1 914 803-byte
/// transfer measured against the MK3.5 on 2026-07-28, and one near the ceiling of the <c>uint32</c>
/// file size the protocol can express.
/// </para>
/// </remarks>
public class KeystreamFixtureTests
{
    /// <summary>
    /// Our keystream at each fixture's offset, byte for byte. This is the whole point of the file -
    /// AES is not in doubt, the counter seeding is.
    /// </summary>
    [Fact]
    public void OurKeystreamMatchesFirmwaresAtEveryOffset()
    {
        foreach (KeystreamFixture fixture in LoadFixtures())
        {
            // Act
            // CTR is symmetric, so transforming zeroes yields the keystream, exactly as the dumper
            // obtained it from the other side.
            byte[] produced = new byte[fixture.Keystream.Length];

            using TransferCipher cipher = new(fixture.Key, fixture.Iv, fixture.Offset);
            cipher.Transform(produced);

            // Assert
            Convert.ToHexString(produced).Should().Be(
                Convert.ToHexString(fixture.Keystream),
                "firmware's decryptor produces this at offset {0} for vector '{1}'",
                fixture.Offset,
                fixture.Name);
        }
    }

    /// <summary>
    /// The fixture has to contain the offsets it claims to, or the test above passes by covering
    /// nothing interesting. A regenerated fixture that quietly lost its non-zero offsets would
    /// otherwise still be green.
    /// </summary>
    [Fact]
    public void TheFixtureCoversMoreThanOffsetZero()
    {
        IReadOnlyList<KeystreamFixture> fixtures = LoadFixtures();

        fixtures.Should().HaveCountGreaterThan(4);
        fixtures.Where(fixture => fixture.Offset > 0).Should().HaveCountGreaterThan(3);

        // The byte boundaries of the block index, which is where a shift written the wrong way round
        // shows up. 4096/16 = 0x100 and 1048576/16 = 0x10000.
        fixtures.Select(fixture => fixture.Offset).Should().Contain([0, 4096, 1048576]);
    }

    /// <summary>
    /// Chunking must not change the output: the endpoint will write a range in whatever sizes the
    /// response pipeline hands it, and firmware's own decryptor is fed whatever a packet carried.
    /// </summary>
    /// <remarks>
    /// Only the first offset has to be block-aligned. This drives the keystream buffering across
    /// call boundaries that fall mid-block, which is the part of <see cref="TransferCipher"/> the
    /// fixture comparison alone would not exercise - the dumper's own varying chunk sizes prove the
    /// same property on the firmware side.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(37)]
    public void TransformingInChunksMatchesTransformingInOneGo(int chunkSize)
    {
        foreach (KeystreamFixture fixture in LoadFixtures())
        {
            // Act
            byte[] chunked = new byte[fixture.Keystream.Length];

            using TransferCipher cipher = new(fixture.Key, fixture.Iv, fixture.Offset);

            for (int position = 0; position < chunked.Length; position += chunkSize)
            {
                cipher.Transform(chunked.AsSpan(position, Math.Min(chunkSize, chunked.Length - position)));
            }

            // Assert
            Convert.ToHexString(chunked).Should().Be(
                Convert.ToHexString(fixture.Keystream),
                "vector '{0}' in {1}-byte chunks",
                fixture.Name,
                chunkSize);
        }
    }

    /// <summary>
    /// An unaligned offset is a bug on our side rather than a case to support: firmware asserts the
    /// same alignment, so it would abort on a real printer rather than decrypt badly.
    /// </summary>
    [Fact]
    public void AnUnalignedOffsetIsRefused()
    {
        byte[] key = new byte[TransferCipher.KeyLength];
        byte[] iv = new byte[TransferCipher.IvLength];

        Action construct = () =>
        {
            using TransferCipher cipher = new(key, iv, 8);
        };

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AKeyOfTheWrongLengthIsRefused()
    {
        byte[] iv = new byte[TransferCipher.IvLength];

        Action construct = () =>
        {
            using TransferCipher cipher = new(new byte[8], iv, 0);
        };

        construct.Should().Throw<ArgumentException>();
    }

    private static IReadOnlyList<KeystreamFixture> LoadFixtures()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText("keystream-fixtures.json"));

        return document.RootElement.EnumerateArray()
            .Select(entry => new KeystreamFixture(
                entry.GetProperty("name").GetString()!,
                Convert.FromHexString(entry.GetProperty("key").GetString()!),
                Convert.FromHexString(entry.GetProperty("iv").GetString()!),
                entry.GetProperty("offset").GetInt64(),
                Convert.FromHexString(entry.GetProperty("keystream").GetString()!)))
            .ToList();
    }

    private sealed record KeystreamFixture(string Name, byte[] Key, byte[] Iv, long Offset, byte[] Keystream);
}
