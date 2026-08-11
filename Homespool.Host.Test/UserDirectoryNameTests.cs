using System.Linq;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.Test;

/// <summary>
/// The decorative half of a user's directory name - <c>12-Sørensen</c> - which exists so that a
/// person poking around the data directory can see whose files are whose.
/// </summary>
/// <remarks>
/// The id prefix carries every correctness property, so these tests are about legibility and about
/// the short list of things that would break a filesystem, a shell, or a reader.
/// </remarks>
public class UserDirectoryNameTests
{
    [Theory]

    // The case the whole design is for: a name that is merely not English survives intact. An ASCII
    // allowlist would have written "12-S-rensen" here.
    [InlineData("Sørensen", "12-Sørensen")]
    [InlineData("北村", "12-北村")]
    [InlineData("Ægir", "12-Ægir")]
    [InlineData("alice", "12-alice")]

    // Spaces are fine in a path and read better than an underscore.
    [InlineData("Bob C", "12-Bob C")]
    public void AUnicodeNameIsKept(string displayName, string expected)
    {
        UserDirectoryName.For(12, displayName).Should().Be(expected);
    }

    [Theory]

    // Separators, or the name would express a directory of its own.
    [InlineData("a/b", "12-a-b")]
    [InlineData("a\\b", "12-a-b")]
    [InlineData("a:b", "12-a-b")]

    // A newline in a directory name breaks shell pipelines and makes log lines lie.
    [InlineData("a\nb", "12-a-b")]
    [InlineData("a\tb", "12-a-b")]

    // Windows silently trims these, so the same name would be two directories depending on where it
    // was created.
    [InlineData("alice.", "12-alice")]
    [InlineData("alice ", "12-alice")]
    public void WhatWouldBreakAFilesystemOrAShellIsReplaced(string displayName, string expected)
    {
        UserDirectoryName.For(12, displayName).Should().Be(expected);
    }

    /// <summary>
    /// A right-to-left override can make a listing render deceptively, which matters precisely
    /// because these names exist to be read by a human.
    /// </summary>
    [Fact]
    public void BidiAndZeroWidthMarksAreReplaced()
    {
        UserDirectoryName.For(12, "gpj‮exe").Should().Be("12-gpj-exe");
        UserDirectoryName.For(12, "ali​ce").Should().Be("12-ali-ce");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]

    // Windows refuses these outright, whatever the extension.
    [InlineData("CON")]
    [InlineData("nul")]
    public void ANameThatSurvivesAsNothingLeavesTheBareId(string? displayName)
    {
        UserDirectoryName.For(12, displayName).Should().Be("12");
    }

    /// <summary>
    /// The cap is in bytes, not characters: ext4 allows 255 bytes per component and a single emoji
    /// is four of them.
    /// </summary>
    [Fact]
    public void TheSuffixIsCappedInBytesAndNeverSplitsACharacter()
    {
        string emoji = string.Concat(Enumerable.Repeat("\U0001F642", 40));

        string name = UserDirectoryName.For(12, emoji);
        string suffix = name["12-".Length..];

        Encoding.UTF8.GetByteCount(suffix)
                .Should().BeLessThanOrEqualTo(UserDirectoryName.MaxSuffixBytes);

        // Whole characters only - a halved surrogate pair would render as a replacement character.
        suffix.Should().NotContain("�");
        suffix.EnumerateRunes().Should().OnlyContain(rune => rune.Value == 0x1F642);
    }

    /// <summary>
    /// The glob has to be unambiguous across ids, since it is the only thing resolution uses.
    /// </summary>
    [Fact]
    public void ThePatternMatchesOnlyItsOwnId()
    {
        UserDirectoryName.PatternFor(12).Should().Be("12-*");

        // The hyphen is what stops 12-* claiming 120-bob, and is why it is the separator.
        UserDirectoryName.For(120, "bob").Should().StartWith("120-");
        UserDirectoryName.For(120, "bob").Should().NotStartWith("12-");
    }

    /// <summary>
    /// Only the first hyphen is significant, so a display name may contain its own.
    /// </summary>
    [Fact]
    public void ANameMayContainAHyphen()
    {
        UserDirectoryName.For(12, "anna-lena").Should().Be("12-anna-lena");
    }
}
