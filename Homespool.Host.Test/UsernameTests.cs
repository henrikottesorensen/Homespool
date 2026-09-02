using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;

using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The username - what someone signs in with, and what the interface calls them.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>DisplayNameTests</c>, which covered the display-only half of "the username should not
/// be the email address". That half is gone: the account's own <c>UserName</c> now carries the name,
/// so there is one name rather than two and nothing left to seed from an address.
/// </para>
/// <para>
/// The character set is asserted here rather than trusted, because it is the whole reason sign-in can
/// take either identifier in one field without the two namespaces overlapping.
/// </para>
/// </remarks>
public class UsernameTests
{
    /// <summary>
    /// No <c>@</c>, so a username can never be shaped like an address - which is what makes
    /// <c>LoginModel</c>'s username-then-email resolution unambiguous rather than merely ordered.
    /// </summary>
    [Fact]
    public void AUsernameMayNotContainAnAtSign()
    {
        HSUser.AllowedUsernameCharacters.Should().NotContain("@");
    }

    /// <summary>The characters people actually put in a handle are all there.</summary>
    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('7')]
    [InlineData('-')]
    [InlineData('.')]
    [InlineData('_')]
    public void AUsernameMayContainLettersDigitsAndTheThreePunctuationMarks(char character)
    {
        HSUser.AllowedUsernameCharacters.Should().Contain(character.ToString());
    }

    /// <summary>
    /// The letters people are actually called by, across the Latin alphabets: Danish, German, Spanish,
    /// Polish, Czech, Slovak, Turkish, Romanian, Vietnamese, French - and every letter of a living
    /// alphabet that Unicode's confusables table would have taken, Icelandic's two included.
    /// </summary>
    [Theory]
    [InlineData('æ')]
    [InlineData('ø')]
    [InlineData('å')]
    [InlineData('Æ')]
    [InlineData('Ø')]
    [InlineData('Å')]
    [InlineData('é')]
    [InlineData('ü')]
    [InlineData('ñ')]
    [InlineData('ß')]
    [InlineData('ẞ')]
    [InlineData('ł')]
    [InlineData('ě')]
    [InlineData('ľ')]
    [InlineData('ĺ')]
    [InlineData('ğ')]
    [InlineData('ş')]
    [InlineData('ı')]
    [InlineData('ș')]
    [InlineData('ț')]
    [InlineData('Đ')]
    [InlineData('đ')]
    [InlineData('Ð')]
    [InlineData('ð')]
    [InlineData('Þ')]
    [InlineData('þ')]
    [InlineData('ễ')]
    [InlineData('Œ')]
    public void AUsernameMayContainTheLettersOfTheLatinAlphabets(char character)
    {
        HSUser.AllowedUsernameCharacters.Should().Contain(character.ToString());
    }

    /// <summary>
    /// Dotless i is accepted on the living-alphabet rule alone, not for free: Unicode's simple case
    /// mapping takes ı to I, but .NET's invariant casing deliberately leaves the Turkish i pair alone,
    /// so Identity's normalised names differ and <c>yıldız</c> and <c>yildiz</c> can be two accounts.
    /// Pinned so nobody re-derives the "free" argument from the Unicode data.
    /// </summary>
    [Fact]
    public void ADotlessIIsADistinctNameToTheNormaliser()
    {
        UpperInvariantLookupNormalizer normaliser = new();

        normaliser.NormalizeName("yıldız").Should().NotBe(normaliser.NormalizeName("yildiz"));
    }

    /// <summary>
    /// What stays out: the lookalikes Unicode's confusables table names among archaic, phonetic and
    /// transliteration letters, the compatibility digraphs, anything outside the Latin blocks, and
    /// the marks that would let a name be spelled two ways.
    /// </summary>
    [Theory]
    [InlineData('ſ', "long s reads as f")]
    [InlineData('ƒ', "reads as f")]
    [InlineData('ƿ', "wynn reads as p")]
    [InlineData('ĕ', "reads as ě, which is Czech's")]
    [InlineData('ǎ', "a pinyin tone mark, reads as ă")]
    [InlineData('Ĳ', "reads as IJ")]
    [InlineData('ǆ', "reads as dž")]
    [InlineData('ǅ', "a titlecase digraph is neither upper- nor lower-case")]
    [InlineData('µ', "the micro sign sits below the first Latin-1 letter")]
    [InlineData('α', "Greek")]
    [InlineData('а', "Cyrillic a")]
    [InlineData('+', "an address character")]
    [InlineData(' ', "whitespace")]
    [InlineData('\u0301', "a combining acute")]
    [InlineData('\u200D', "a zero-width joiner")]
    public void AUsernameMayNotContainLookalikesOrOtherScripts(char character, string because)
    {
        HSUser.AllowedUsernameCharacters.Should().NotContain(character.ToString(), because);
    }

    /// <summary>
    /// Everything beyond ASCII is a cased letter from one of the four Latin blocks - the property the
    /// examples above sample, stated once over the whole set.
    /// </summary>
    [Fact]
    public void EveryNonAsciiCharacterIsACasedLatinLetter()
    {
        foreach (char character in HSUser.AllowedUsernameCharacters.Where(c => c > 0x7F))
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);

            category.Should().BeOneOf([UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter],
                                      $"U+{(int)character:X4} is in the allowed set");

            bool inLatinBlock = character is (>= '\u00C0' and <= '\u024F') or (>= '\u1E00' and <= '\u1EFF');

            inLatinBlock.Should().BeTrue($"U+{(int)character:X4} is in the allowed set");
        }
    }

    /// <summary>
    /// The app API's <c>Name</c> is the username. An API called <c>Name</c> should not hand out an
    /// address, and it no longer has to: the account has a name of its own.
    /// </summary>
    [Fact]
    public void TheAppApiReturnsTheUsername()
    {
        HSUser user = new() { UserName = "henrik", Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("henrik");
    }

    /// <summary>
    /// The fallback exists only because <c>UserName</c> is nullable on the base type. Nothing this
    /// application creates gets there - but a blank name would render as a blank greeting, and the
    /// address is the one other identifier always present.
    /// </summary>
    [Fact]
    public void TheAppApiFallsBackToTheEmailWhenThereIsNoUsername()
    {
        HSUser user = new() { Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("rig@example.com");
    }
}
