using System;
using System.Globalization;

using AwesomeAssertions;

using Homespool.Host.Localisation;

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
    [InlineData("en-GB", "4.1 MiB")]
    [InlineData("en-US", "4.1 MiB")]
    [InlineData("da", "4,1 MiB")]
    public void AFileSizeUsesTheSeparatorTheReaderWritesNumbersWith(string culture, string expected)
    {
        InCulture(culture, () => ByteSize.Format(4_300_000, TestLocaliser.Shared()).Should().Be(expected));
    }

    /// <summary>
    /// Under 1 KiB there is no decimal to separate, so every culture agrees - worth pinning, because
    /// it is the branch that would go on passing if the other one regressed.
    /// </summary>
    [Theory]
    [InlineData("en-GB")]
    [InlineData("da")]
    public void AWholeNumberOfBytesReadsTheSameEverywhere(string culture)
    {
        InCulture(culture, () => ByteSize.Format(512, TestLocaliser.Shared()).Should().Be("512 B"));
    }

    /// <summary>
    /// The unit is a resource string, so it can differ by language - and today does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This reverses what this file used to assert</b> (Henrik, 2026-09-01), and the reversal is
    /// worth stating because the old reasoning was not silly. It held that a unit is ours rather than
    /// the reader's, and that <c>4,1 Mo</c> against <c>4.1 MB</c> would be two different claims about
    /// one file. What decides it the other way is that in French the unit genuinely is a word in that
    /// language - <c>Mio</c> for this quantity - so refusing to translate it does not keep the claim
    /// identical, it just states it in somebody else's language beside a separator in theirs.
    /// </para>
    /// <para>
    /// <b>No shipped language differs</b>, so this pins that English and Danish agree. It is paid
    /// before the first language that would disagree rather than after, which is the only time such a
    /// thing is cheap.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("da")]
    public void EveryShippedLanguageWritesTheSameUnitToday(string culture)
    {
        InCulture(culture, () => ByteSize.Format(4_300_000, TestLocaliser.Shared()).Should().EndWith(" MiB"));
    }

    /// <summary>
    /// The prefixes are IEC because the arithmetic is binary. Calling 1024-based units MB named a
    /// quantity this never produced.
    /// </summary>
    [Fact]
    public void ThePrefixesMatchTheArithmetic()
    {
        InCulture("en-GB", () =>
        {
            ByteSize.Format(1024, TestLocaliser.Shared()).Should().Be("1 KiB");
            ByteSize.Format(1024L * 1024, TestLocaliser.Shared()).Should().Be("1 MiB");
            ByteSize.Format(1024L * 1024 * 1024, TestLocaliser.Shared()).Should().Be("1 GiB");
        });
    }
}
