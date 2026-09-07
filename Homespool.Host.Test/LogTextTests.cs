using AwesomeAssertions;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// The rule that stands between a caller-supplied string and a log line: a value whose every byte
/// the sender chose cannot forge a record, repaint a terminal, or render as something it is not.
/// </summary>
/// <remarks>
/// Every case here is a value an anonymous caller can put on the wire - <c>POST /p/register</c>'s
/// serial, model and firmware are three of them - so "no real printer sends this" is not a defence.
/// The escapes are written rather than pasted: a source file holding these literally is unreadable
/// in a diff, and carries the very hazard it is testing.
/// </remarks>
public class LogTextTests
{
    /// <summary>The one that matters in a line-oriented log: a newline cannot end a record early.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u0000")]
    [InlineData("\u007F")]
    public void AControlCharacterIsReplaced(string control)
    {
        LogText.Clean("MK4" + control + "IS4").Should().Be("MK4\uFFFDIS4");
    }

    /// <summary>
    /// A terminal escape sequence loses the character that makes it one, and keeps the rest as the
    /// visible text it always was.
    /// </summary>
    /// <remarks>
    /// The reader of a log is the target here rather than the file: <c>ESC[2J</c> clears their screen
    /// and <c>ESC[31m</c> paints whatever follows, neither of which needs the value to be long or
    /// interesting.
    /// </remarks>
    [Fact]
    public void AnEscapeSequenceLosesItsEscape()
    {
        LogText.Clean("MK4\u001B[2J\u001B[31mready").Should().Be("MK4\uFFFD[2J\uFFFD[31mready");
    }

    /// <summary>
    /// The invisible marks, which carry no control character and still let a value render as
    /// something other than what was stored.
    /// </summary>
    [Theory]
    [InlineData("\u200B")]
    [InlineData("\u200C")]
    [InlineData("\u200D")]
    [InlineData("\uFEFF")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\u2069")]
    public void AnInvisibleMarkIsReplaced(string mark)
    {
        LogText.Clean("MK4" + mark).Should().Be("MK4\uFFFD");
    }

    /// <summary>
    /// Replaced one for one, never dropped: a value that arrived dirty must not read back as a clean
    /// one somebody could have sent.
    /// </summary>
    /// <remarks>
    /// This is the assertion that separates cleaning from stripping, and stripping is what the
    /// obvious implementation does. A serial of <c>MK4</c> plus a newline, logged as <c>MK4</c>,
    /// hides the only interesting fact about that request.
    /// </remarks>
    [Fact]
    public void ADirtyValueDoesNotBecomeACleanOne()
    {
        string cleaned = LogText.Clean("MK4\n");

        cleaned.Should().NotBe("MK4");
        cleaned.Should().HaveLength(4);
    }

    /// <summary>Everything merely non-English survives - the exclusion is about rendering, not alphabet.</summary>
    [Theory]
    [InlineData("15715-4842441651816441")]
    [InlineData("6.4.0+11974")]
    [InlineData("MINI")]
    [InlineData("Angstrom MK4 halv")]
    public void AnOrdinaryValuePassesThroughUnchanged(string value)
    {
        LogText.Clean(value).Should().Be(value);
    }

    /// <summary>Nothing to say is said as nothing, rather than as a null in the rendered line.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAbsentValueIsEmpty(string? value)
    {
        LogText.Clean(value).Should().BeEmpty();
    }
}
