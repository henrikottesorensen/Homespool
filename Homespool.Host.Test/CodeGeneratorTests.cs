using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

public class CodeGeneratorTests
{
    /// <summary>
    /// The firmware copies the <c>Code</c> response header into a fixed buffer and silently drops
    /// anything past it: <c>registrator.hpp</c> declares <c>CODE_SIZE = 25</c> and <c>ExtractCode</c>
    /// guards with <c>code_len &lt; CODE_SIZE</c>.
    /// </summary>
    private const int FirmwareCodeSizeLimit = 25;

    private readonly CodeGenerator _generator = new();

    /// <summary>
    /// The constraint with no other enforcement anywhere in the codebase.
    /// </summary>
    /// <remarks>
    /// Exceeding it does not fail loudly. The firmware truncates the code, then polls forever with a
    /// value the server never issued, so registration hangs rather than errors. Nothing in
    /// <see cref="CodeGenerator"/> references the firmware limit, so only this test stands between a
    /// one-character edit and every enrollment breaking.
    /// </remarks>
    [Fact]
    public void GeneratedCodeFitsInsideTheFirmwareBuffer()
    {
        // Assert
        for (int i = 0; i < 50; i++)
        {
            _generator.GenerateCode($"15715-{i}").Length.Should()
                      .BeLessThanOrEqualTo(FirmwareCodeSizeLimit,
                                           "registrator.hpp truncates past CODE_SIZE and the printer "
                                           + "then polls with a code the server never issued");
        }
    }

    /// <summary>
    /// The code contains only characters that survive an HTTP header and a human retyping them.
    /// </summary>
    /// <remarks>
    /// It travels in a <c>Code</c> response header and is then read off the printer's screen by a
    /// person claiming it, so anything needing escaping or easily mistyped would be a poor choice.
    /// Prusa's own codes in enrol.cap use the same alphabet.
    /// </remarks>
    [Fact]
    public void GeneratedCodeUsesOnlyUppercaseBase36()
    {
        // Assert
        // The capture shows Prusa's own codes in this alphabet, and it keeps the code safe to carry
        // in an HTTP header and read aloud off a printer screen.
        _generator.GenerateCode("15715-4842441651816441")
                  .Should().MatchRegex("^[0-9A-Z]+$");
    }

    /// <summary>
    /// Two codes for the same printer differ, because a nonce is mixed into the hash.
    /// </summary>
    /// <remarks>
    /// Without it the code would be a pure function of the serial number - which is printed on the
    /// machine and sent in the registration body - so anyone who knew the serial could compute the
    /// credential that claims the printer.
    /// </remarks>
    [Fact]
    public void SameSerialProducesADifferentCodeEachTime()
    {
        // Act
        // A nonce is mixed in, so a code is not derivable from the serial number - which is printed
        // on the machine and travels in the registration body.
        string first = _generator.GenerateCode("15715-4842441651816441");
        string second = _generator.GenerateCode("15715-4842441651816441");

        // Assert
        second.Should().NotBe(first);
    }

    /// <summary>
    /// A smoke check that the code space is not trivially small.
    /// </summary>
    /// <remarks>
    /// <c>TemporaryCode</c> is deliberately non-uniquely indexed and <c>GetToken</c> looks it up with
    /// <c>SingleOrDefaultAsync</c>, so a collision surfaces as a 400 rather than as one printer
    /// receiving another's token. Not proof of uniqueness - a guard against a regression that
    /// shortened or weakened the code.
    /// </remarks>
    [Fact]
    public void CodesDoNotCollideAcrossManyGenerations()
    {
        // Act
        // TemporaryCode is deliberately non-uniquely indexed, and GetToken uses SingleOrDefaultAsync,
        // so a collision surfaces as a 400 rather than a wrong lookup. This is a smoke check that the
        // space is not trivially small.
        HashSet<string> codes = [.. Enumerable.Range(0, 2_000).Select(i => _generator.GenerateCode($"sn-{i}"))];

        // Assert
        codes.Should().HaveCount(2_000);
    }

    /// <summary>
    /// Generating a code works on this platform at all.
    /// </summary>
    /// <remarks>
    /// <c>SHA3_384.Create()</c> used to be called unguarded. .NET defers hashing to the OS, and SHA-3
    /// exists only on Windows 11 build 25324+ and Linux with OpenSSL 1.1.1+ - never on macOS, where
    /// .NET 10 also removed the last OpenSSL fallbacks. So this threw
    /// <see cref="PlatformNotSupportedException"/> on macOS and took registration with it, while
    /// passing in CI and in the Debian container image.
    /// <para>
    /// This test only proves the algorithm resolves <i>here</i>. On a SHA-3 capable box it exercises
    /// SHA-3 and says nothing about the fallback, which is what
    /// <see cref="EitherHashAlgorithmProducesEnoughBase36CharactersToTruncate"/> is for.
    /// </para>
    /// </remarks>
    [Fact]
    public void GeneratingACodeDoesNotThrowOnThisPlatform()
    {
        // Arrange
        Action generate = () => _generator.GenerateCode("15715-4842441651816441");

        // Assert
        generate.Should().NotThrow<PlatformNotSupportedException>(
            "SHA-3 is absent on macOS and on Linux without OpenSSL 1.1.1+, so the algorithm has to be "
            + "chosen against SHA3_384.IsSupported rather than assumed");
    }

    /// <summary>
    /// A code is exactly <c>CodeLength</c> characters, not merely within the firmware's buffer.
    /// </summary>
    [Fact]
    public void GeneratedCodeIsExactlyTheExpectedLength()
    {
        // Assert
        for (int i = 0; i < 50; i++)
        {
            _generator.GenerateCode($"15715-{i}").Length.Should().Be(24);
        }
    }

    /// <summary>
    /// Either permitted digest encodes to at least a full code's worth of base36 characters.
    /// </summary>
    /// <remarks>
    /// The generator truncates the encoded digest to <c>CodeLength</c>, which is only safe if the
    /// encoding is always at least that long. Both algorithms produce 48 bytes, so both encode to
    /// about 75 base36 characters and there is plenty of headroom - but the fallback path cannot be
    /// executed on a machine that has SHA-3, so the property is asserted directly against both
    /// digests instead. Without this, a macOS-only truncation failure would be invisible to CI.
    /// </remarks>
    [Fact]
    public void EitherHashAlgorithmProducesEnoughBase36CharactersToTruncate()
    {
        // Arrange
        byte[] input = Encoding.UTF8.GetBytes("15715-4842441651816441");

        // Assert
        SimpleBase.Base36.UpperCase.Encode(SHA384.HashData(input)).Length.Should().BeGreaterThanOrEqualTo(24);

        if (SHA3_384.IsSupported)
        {
            SimpleBase.Base36.UpperCase.Encode(SHA3_384.HashData(input)).Length.Should().BeGreaterThanOrEqualTo(24);
        }
    }
}
