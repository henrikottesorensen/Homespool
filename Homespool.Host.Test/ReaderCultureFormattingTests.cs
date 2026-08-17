using System;
using System.Globalization;

using AwesomeAssertions;

using Homespool.Host.Pages.Files;

namespace Homespool.Host.Test;

/// <summary>
/// Numbers a person reads, rendered in the culture of the person reading them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of localisation that no resource file can carry.</b> A key holds the words;
/// the separator inside <c>4,1 MB</c> is not in the key at all, and a Danish page can be word-perfect
/// and still write its numbers in English. Which is what it did: <c>FormatSize</c> was invariant, on
/// the reasoning that the separator should not move with the server's locale - the right instinct
/// answered on the wrong axis, because the separator was never supposed to follow a machine in
/// either direction.
/// </para>
/// <para>
/// <b>Dates are deliberately not here.</b> Timestamps stay ISO, and the argument is in
/// <c>Files/Index.cshtml</c> beside the one that renders them: <c>en-GB</c> and <c>en-US</c> are both
/// supported and disagree silently about <c>01/02/2026</c>, so following the reader is the thing that
/// would make a date worse rather than better. Numbers have no such trap - <c>4,1</c> is unambiguous
/// to a Dane and <c>4.1</c> to an English reader, and neither can be misread as the other.
/// </para>
/// </remarks>
public class ReaderCultureFormattingTests
{
    /// <summary>
    /// Runs <paramref name="body"/> with <see cref="CultureInfo.CurrentCulture"/> set, and puts the
    /// old one back afterwards so an xUnit worker thread is not left carrying it into another test.
    /// </summary>
    private static void InCulture(string culture, Action body)
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("en-GB", "4.1 MB")]
    [InlineData("en-US", "4.1 MB")]
    [InlineData("da", "4,1 MB")]
    public void AFileSizeUsesTheSeparatorTheReaderWritesNumbersWith(string culture, string expected)
    {
        InCulture(culture, () => IndexModel.FormatSize(4_300_000).Should().Be(expected));
    }

    /// <summary>
    /// Under 1 KB there is no decimal to separate, so every culture agrees - worth pinning, because
    /// it is the branch that would go on passing if the other one regressed.
    /// </summary>
    [Theory]
    [InlineData("en-GB")]
    [InlineData("da")]
    public void AWholeNumberOfBytesReadsTheSameEverywhere(string culture)
    {
        InCulture(culture, () => IndexModel.FormatSize(512).Should().Be("512 B"));
    }

    /// <summary>
    /// The binary units are ours and are not translated, so they must survive the culture switch
    /// unchanged - a size that read <c>4,1 Mo</c> in one language and <c>4.1 MB</c> in another would
    /// be two different claims about the same file.
    /// </summary>
    [Fact]
    public void TheUnitIsOursAndDoesNotMoveWithTheReader()
    {
        InCulture("da", () => IndexModel.FormatSize(4_300_000).Should().EndWith(" MB"));
        InCulture("en-GB", () => IndexModel.FormatSize(4_300_000).Should().EndWith(" MB"));
    }
}
