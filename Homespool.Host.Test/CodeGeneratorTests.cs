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
    /// The code contains only Crockford base32 characters - in particular, never I, L, O or U.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that makes the 2026-07-28 failure unrepresentable rather than merely
    /// less likely. Base36 contains every confusable pair at once - O/0 (which cost a real
    /// enrollment), and I/1, S/5, B/8 waiting their turn. Crockford omits I, L, O and U from the
    /// alphabet entirely, so a code can never contain the character that was misread.
    /// </para>
    /// <para>
    /// The class range is written out rather than expressed as "not I, L, O, U", so that an encoder
    /// that started emitting lowercase, padding, or a different flavour of base32 fails here too.
    /// Verified against SimpleBase's own <c>Base32Alphabet.cs</c>:
    /// <c>0123456789ABCDEFGHJKMNPQRSTVWXYZ</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void GeneratedCodeUsesOnlyCrockfordBase32()
    {
        // Assert
        for (int i = 0; i < 50; i++)
        {
            _generator.GenerateCode($"15715-{i}")
                      .Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]+$",
                                           "Crockford base32 omits I, L, O and U, which is what makes "
                                           + "the O-for-0 misread that cost a real enrollment impossible");
        }
    }

    /// <summary>
    /// The alphabet genuinely excludes the four confusable letters, asserted directly.
    /// </summary>
    /// <remarks>
    /// The range in <see cref="GeneratedCodeUsesOnlyCrockfordBase32"/> is easy to get subtly wrong
    /// when editing - <c>P-T</c> and <c>V-Z</c> are exactly the spans that keep U out - so this
    /// states the property in the form a reader actually cares about, over enough codes to be sure.
    /// </remarks>
    [Fact]
    public void GeneratedCodeNeverContainsTheConfusableLetters()
    {
        // Act
        string generated = string.Concat(Enumerable.Range(0, 200).Select(i => _generator.GenerateCode($"sn-{i}")));

        // Assert
        generated.Should().NotContainAny("I", "L", "O", "U");
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
    /// A code is exactly ten characters, not merely within the firmware's buffer.
    /// </summary>
    /// <remarks>
    /// Ten is the usability figure, and it is what a person retypes off a low-resolution LCD - the
    /// only way a Homespool user can ever enter one, since the printer's QR is hardcoded to Prusa's
    /// servers. It is also exactly the length Prusa's own servers issue.
    /// </remarks>
    [Fact]
    public void GeneratedCodeIsExactlyTenCharacters()
    {
        // Assert
        for (int i = 0; i < 50; i++)
        {
            _generator.GenerateCode($"15715-{i}").Length.Should().Be(10);
        }
    }

    /// <summary>
    /// The digest encodes to at least a full code's worth of base32 characters.
    /// </summary>
    /// <remarks>
    /// The generator truncates the encoded digest to <c>CodeLength</c>, which is only safe if the
    /// encoding is always at least that long. SHA-384 produces 48 bytes, so it encodes to 77
    /// Crockford characters - ample headroom, and this is what keeps a future change to either the
    /// digest or the code length from silently truncating past the end.
    /// </remarks>
    [Fact]
    public void TheDigestEncodesToEnoughBase32CharactersToTruncate()
    {
        // Arrange
        byte[] input = Encoding.UTF8.GetBytes("15715-4842441651816441");

        // Assert
        SimpleBase.Base32.Crockford.Encode(SHA384.HashData(input)).Length.Should().BeGreaterThanOrEqualTo(10);
    }
}
