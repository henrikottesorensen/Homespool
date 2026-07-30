using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The forgiving half of the claim-code change: <see cref="CodeGenerator"/> stops <i>producing</i>
/// confusable characters, and this stops a person <i>typing</i> one from mattering.
/// </summary>
public class ClaimCodeTests
{
    /// <summary>
    /// The failure that actually happened: an <c>O</c> read off the printer's screen for a <c>0</c>.
    /// </summary>
    /// <remarks>
    /// On 2026-07-28 a real MK3.5 enrolment failed exactly this way, and it cost a round of
    /// log-reading and one wrong diagnosis before the character was identified - because a wrong
    /// code is indistinguishable from an unclaimed one. Crockford's substitutions are what make it
    /// stop being a failure at all. This is the single most important assertion in the file.
    /// </remarks>
    [Theory]
    [InlineData("O", "0")]
    [InlineData("o", "0")]
    [InlineData("I", "1")]
    [InlineData("i", "1")]
    [InlineData("L", "1")]
    [InlineData("l", "1")]
    public void ConfusableLettersResolveToTheDigitTheyWereMisreadFor(string typed, string expected)
    {
        // Assert
        ClaimCode.Normalise(typed).Should().Be(expected);
    }

    /// <summary>
    /// A code typed in lowercase still matches, since the stored form is uppercase.
    /// </summary>
    [Fact]
    public void LowercaseIsUppercased()
    {
        // Assert
        ClaimCode.Normalise("muf4rzjf5r").Should().Be("MUF4RZJF5R");
    }

    /// <summary>
    /// The separators people insert when chunking a code are ignored.
    /// </summary>
    /// <remarks>
    /// Nothing displays the code grouped, but people group them anyway when copying by hand, and a
    /// paste out of a chat client can arrive with either. Silently accepting both costs nothing.
    /// </remarks>
    [Theory]
    [InlineData("MUF4R-ZJF5R")]
    [InlineData("MUF4R ZJF5R")]
    [InlineData("  MUF4RZJF5R  ")]
    [InlineData("MUF4R\tZJF5R")]
    public void SeparatorsAndSurroundingWhitespaceAreStripped(string typed)
    {
        // Assert
        ClaimCode.Normalise(typed).Should().Be("MUF4RZJF5R");
    }

    /// <summary>
    /// Everything applies at once, which is the realistic case.
    /// </summary>
    [Fact]
    public void TheSubstitutionsCasingAndSeparatorsAllApplyTogether()
    {
        // Assert
        // Every confusable letter, lowercased, hyphenated and padded - all of which a person could
        // plausibly produce in one go.
        ClaimCode.Normalise(" muf4o-zilf5 ").Should().Be("MUF40Z11F5");
    }

    /// <summary>
    /// A genuinely wrong code is not cleaned into a plausible-looking one.
    /// </summary>
    /// <remarks>
    /// Only separators are dropped. Stripping every character outside the alphabet would turn a
    /// mistyped code into a shorter valid-looking one and hide the mistake - and, worse, could make
    /// two different wrong inputs collide onto one real code.
    /// </remarks>
    [Fact]
    public void CharactersOutsideTheAlphabetAreKeptRatherThanStripped()
    {
        // Assert
        ClaimCode.Normalise("MUF4$ZJF5R").Should().Be("MUF4$ZJF5R");
    }

    /// <summary>
    /// <c>U</c> has no Crockford substitution, so it is left alone and simply fails to match.
    /// </summary>
    /// <remarks>
    /// Crockford excludes U from the alphabet but defines no mapping for it, unlike O, I and L.
    /// Inventing one here would be this codebase disagreeing with the spec it cites.
    /// </remarks>
    [Fact]
    public void UIsLeftAloneBecauseCrockfordDefinesNoSubstitutionForIt()
    {
        // Assert
        ClaimCode.Normalise("u").Should().Be("U");
    }

    /// <summary>
    /// Null and empty normalise to empty rather than throwing.
    /// </summary>
    /// <remarks>
    /// The page's <c>[Required]</c> catches these first, but this runs before the lookup and must
    /// not be the thing that throws if validation is ever reordered.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyNormalisesToEmpty(string? typed)
    {
        // Assert
        ClaimCode.Normalise(typed).Should().BeEmpty();
    }

    /// <summary>
    /// An already-canonical code is unchanged, so normalising the printer's own issued code is inert.
    /// </summary>
    /// <remarks>
    /// This is what makes it safe that the human path normalises and the printer's poll does not:
    /// the two agree on anything the server actually issued, so the paths cannot drift into
    /// disagreeing about what a valid code is.
    /// </remarks>
    [Fact]
    public void AnIssuedCodeIsAlreadyCanonical()
    {
        // Arrange
        string issued = new CodeGenerator().GenerateCode("15715-4842441651816441");

        // Assert
        ClaimCode.Normalise(issued).Should().Be(issued);
    }
}
