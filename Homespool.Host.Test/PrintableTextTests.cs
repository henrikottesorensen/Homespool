using System;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// The one definition of what may not be in a stranger's string, and the two things done about it:
/// answering whether a value is clean, and replacing what is not.
/// </summary>
/// <remarks>
/// <c>LogTextTests</c> walks the character set through the log's own entry point, which is this
/// underneath. What is pinned here is what that suite cannot see: the question asked on its own, for
/// the callers that refuse rather than replace, and the cut that adds nothing, for the values that
/// go on being used as themselves. The escapes are written rather than pasted, as there.
/// </remarks>
public class PrintableTextTests
{
    [Theory]
    [InlineData("MK4\u000A")]
    [InlineData("MK4\u001B[2J")]
    [InlineData("MK4\u200B")]
    [InlineData("MK4\uFEFF")]
    [InlineData("MK4\u202E")]
    [InlineData("MK4\u2066")]
    public void AValueHoldingAnUnprintableCharacterIsNotPrintable(string value)
    {
        PrintableText.IsPrintable(value).Should().BeFalse();
    }

    /// <summary>
    /// Each group listed whole: the separators, every <c>Bidi_Control</c> character, and the ones with
    /// no width or no ink - with both ends of each range, since a range is where a member goes missing.
    /// </summary>
    [Theory]
    [InlineData('\u007F')]
    [InlineData('\u0085')]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    [InlineData('\u061C')]
    [InlineData('\u200E')]
    [InlineData('\u200F')]
    [InlineData('\u202A')]
    [InlineData('\u202E')]
    [InlineData('\u2066')]
    [InlineData('\u2069')]
    [InlineData('\u00AD')]
    [InlineData('\u180E')]
    [InlineData('\uFEFF')]
    [InlineData('\u200B')]
    [InlineData('\u200D')]
    [InlineData('\u2060')]
    [InlineData('\u2064')]
    public void EveryMemberOfEachGroupIsUnprintable(char character)
    {
        PrintableText.IsUnprintable(character).Should().BeTrue();
    }

    /// <summary>
    /// The neighbours of each range, and the reason the rule is a list rather than a category: the
    /// Arabic number sign is a <c>Format</c> character, and it is visible and belongs in a name.
    /// </summary>
    [Theory]
    [InlineData('\u0600')]
    [InlineData('\u200A')]
    [InlineData('\u2010')]
    [InlineData('\u2027')]
    [InlineData('\u202F')]
    [InlineData('\u205F')]
    [InlineData('\u00AE')]
    public void WhatStandsBesideAGroupIsNotInIt(char character)
    {
        PrintableText.IsUnprintable(character).Should().BeFalse();
    }

    /// <summary>
    /// The invisible characters outside the basic plane, both ends of each range: the tag block, which
    /// mirrors ASCII with no ink at all, and the three sets of format controls.
    /// </summary>
    [Theory]
    [InlineData(0xE0000)]
    [InlineData(0xE0001)]
    [InlineData(0xE0041)]
    [InlineData(0xE007F)]
    [InlineData(0x13430)]
    [InlineData(0x1343F)]
    [InlineData(0x1BCA0)]
    [InlineData(0x1BCA3)]
    [InlineData(0x1D173)]
    [InlineData(0x1D17A)]
    public void AnInvisibleCharacterOutsideTheBasicPlaneIsUnprintable(int codePoint)
    {
        PrintableText.IsUnprintable(new Rune(codePoint)).Should().BeTrue();
        PrintableText.IsPrintable("model" + char.ConvertFromUtf32(codePoint)).Should().BeFalse();
    }

    /// <summary>
    /// Their neighbours, an emoji, the Kaithi number sign - a visible <c>Format</c> character, the
    /// reason this is a list out here too - and the variation selectors, invisible and left alone
    /// because one follows nearly every emoji.
    /// </summary>
    [Theory]
    [InlineData(0x1F600)]
    [InlineData(0x110BD)]
    [InlineData(0xFE0F)]
    [InlineData(0xE0100)]
    [InlineData(0x1342F)]
    [InlineData(0x1D172)]
    [InlineData(0x1D17B)]
    public void WhatStandsBesideThemIsNot(int codePoint)
    {
        PrintableText.IsUnprintable(new Rune(codePoint)).Should().BeFalse();
        PrintableText.IsPrintable("model" + char.ConvertFromUtf32(codePoint)).Should().BeTrue();
    }

    /// <summary>
    /// Text nobody can see: each tag character is an ASCII letter with no ink, so a name can carry a
    /// second name inside it. Replaced one for one, a character at a time rather than a char at a time.
    /// </summary>
    [Fact]
    public void TextSpeltInTagCharactersIsReplacedACharacterAtATime()
    {
        string hidden = string.Concat("exe".Select(letter => char.ConvertFromUtf32(0xE0000 + letter)));

        PrintableText.Replace("model" + hidden + ".gcode").Should().Be("model\uFFFD\uFFFD\uFFFD.gcode");
        PrintableText.Replace("model" + hidden + ".gcode", '-').Should().Be("model---.gcode");
    }

    /// <summary>Half a surrogate pair is not a character, whichever half it is and wherever it sits.</summary>
    [Fact]
    public void ASurrogateWithNoPartnerIsUnprintable()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);

        PrintableText.IsPrintable("ok " + emoji).Should().BeTrue();
        PrintableText.IsPrintable("half " + emoji[0]).Should().BeFalse();
        PrintableText.IsPrintable(emoji[1] + " half").Should().BeFalse();
        PrintableText.Replace(emoji[0] + "half" + emoji[1]).Should().Be("\uFFFDhalf\uFFFD");
        PrintableText.Replace(emoji + emoji[0] + emoji).Should().Be(emoji + "\uFFFD" + emoji);
    }

    /// <summary>
    /// Asked about one <see cref="char"/>, a surrogate is unprintable: whoever asks that way has not
    /// paired it, and answering yes is how a rule walks past a character that takes two.
    /// </summary>
    [Fact]
    public void ASurrogateAskedAboutOnItsOwnIsUnprintable()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);

        PrintableText.IsUnprintable(emoji[0]).Should().BeTrue();
        PrintableText.IsUnprintable(emoji[1]).Should().BeTrue();
    }

    /// <summary>
    /// The known cost, pinned so it is a decision rather than a surprise: the joiner in a family emoji
    /// is the zero-width joiner, and a name is refused for it.
    /// </summary>
    [Fact]
    public void AnEmojiBuiltWithAJoinerIsRefusedForTheJoiner()
    {
        string family = char.ConvertFromUtf32(0x1F468) + "\u200D" + char.ConvertFromUtf32(0x1F469);

        PrintableText.IsPrintable(family).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("248289761001")]
    [InlineData("anna.s\u00F8rensen@example.net")]
    [InlineData("\u65E5\u672C\u8A9E")]
    public void AnOrdinaryValueIsPrintable(string value)
    {
        PrintableText.IsPrintable(value).Should().BeTrue();
    }

    /// <summary>
    /// Cut with nothing added: the value is kept and shown as itself, and a marker would become part
    /// of it - somebody's display name ending in a character count.
    /// </summary>
    [Fact]
    public void AnOverLongValueIsCutAndSaysNothingAboutIt()
    {
        PrintableText.Replace("Anna\u001B[2J" + new string('x', 5000), 8).Should().Be("Anna\uFFFD[2J");
    }

    [Fact]
    public void AValueWithinTheBoundComesBackAsItself()
    {
        string value = "Anna S\u00F8rensen";

        PrintableText.Replace(value, 256).Should().BeSameAs(value);
    }

    /// <summary>Half a surrogate pair is not a character, and a cookie or a JSON writer may refuse it.</summary>
    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        string pair = char.ConvertFromUtf32(0x1F600);

        PrintableText.Cut(new string('x', 9) + pair + "yyy", 10).Should().Be(new string('x', 9));
        PrintableText.Cut(new string('x', 8) + pair + "yyy", 10).Should().Be(new string('x', 8) + pair);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ABoundMustBePositive(int maxLength)
    {
        Action bounded = () => PrintableText.Replace("name", maxLength);

        bounded.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NothingIsTheEmptyString()
    {
        PrintableText.Replace(null).Should().BeEmpty();
        PrintableText.Replace(null, 8).Should().BeEmpty();
    }
}
