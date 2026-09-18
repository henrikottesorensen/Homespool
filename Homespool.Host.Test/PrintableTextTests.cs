using System;

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
