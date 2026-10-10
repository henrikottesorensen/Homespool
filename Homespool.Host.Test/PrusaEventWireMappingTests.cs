using System;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaEventWireMapping"/> - the written-out table between Connect's event words and
/// <see cref="PrinterEventType"/>, asserted pair by pair in both directions.
/// </summary>
/// <remarks>
/// A round trip alone would pass a table with two words swapped on both sides, so the pairs are
/// written out again here as the oracle: the words from the Connect SDK's <c>Event</c> enum
/// (<c>prusa/connect/printer/const.py</c>) plus <c>CANCELABLE_CHANGED</c> from Buddy firmware's
/// <c>planner.cpp</c>, each against the value whose documentation describes it.
/// </remarks>
public sealed class PrusaEventWireMappingTests
{
    private static readonly (string word, PrinterEventType value)[] Table =
    [
        ("ACCEPTED", PrinterEventType.Accepted),
        ("REJECTED", PrinterEventType.Rejected),
        ("FAILED", PrinterEventType.Failed),
        ("FINISHED", PrinterEventType.Finished),
        ("INFO", PrinterEventType.Info),
        ("STATE_CHANGED", PrinterEventType.StateChanged),
        ("MEDIUM_EJECTED", PrinterEventType.StorageEjected),
        ("MEDIUM_INSERTED", PrinterEventType.StorageInserted),
        ("FILE_CHANGED", PrinterEventType.FileChanged),
        ("FILE_INFO", PrinterEventType.FileInfo),
        ("JOB_INFO", PrinterEventType.JobInfo),
        ("TRANSFER_INFO", PrinterEventType.TransferInfo),
        ("MESH_BED_DATA", PrinterEventType.MeshBedData),
        ("TRANSFER_ABORTED", PrinterEventType.TransferAborted),
        ("TRANSFER_STOPPED", PrinterEventType.TransferStopped),
        ("TRANSFER_FINISHED", PrinterEventType.TransferFinished),
        ("SLOT_EVENT", PrinterEventType.SlotEvent),
        ("CANCELABLE_CHANGED", PrinterEventType.CancelableChanged),
    ];

    public static TheoryData<string, PrinterEventType> Vocabulary => new(Table);

    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void EveryWireWordParsesToItsValue(string word, PrinterEventType value)
    {
        PrusaEventWireMapping.Parse(word).Should().Be(value);
    }

    /// <summary>
    /// The direction <c>WireType</c> is stored through, so a value formatting to its neighbour's
    /// word would be written into the event log as that word.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void EveryValueFormatsToItsWireWord(string word, PrinterEventType value)
    {
        PrusaEventWireMapping.Format(value).Should().Be(word);
    }

    /// <summary>
    /// A value added to the enum fails here until it has a row, and the row fails the two theories
    /// above until the mapping has both its arms - without them, <see cref="PrusaEventWireMapping.Format"/>
    /// throws the first time the value is stored.
    /// </summary>
    [Fact]
    public void TheTableCoversEveryValueButUndefinedWithItsOwnWord()
    {
        Table.Select(row => row.word).Should().OnlyHaveUniqueItems();
        Table.Select(row => row.value).Should().BeEquivalentTo(
            Enum.GetValues<PrinterEventType>().Where(value => value != PrinterEventType.Undefined),
            "every value but the sentinel is a fact some Connect client reports");
    }

    [Fact]
    public void UndefinedHasNoWireWord()
    {
        Action format = () => PrusaEventWireMapping.Format(PrinterEventType.Undefined);

        format.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The near misses are the ones a mechanical rule would produce: our own member name, its
    /// casing transform, a lowercased wire word, and the spelling our code uses for cancelling.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("StorageInserted")]
    [InlineData("STORAGE_INSERTED")]
    [InlineData("medium_inserted")]
    [InlineData("CANCELLABLE_CHANGED")]
    [InlineData("UNDEFINED")]
    public void AWordOutsideTheVocabularyThrows(string word)
    {
        Action parse = () => PrusaEventWireMapping.Parse(word);

        parse.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void EveryWireWordTryParsesToItsValue(string word, PrinterEventType value)
    {
        PrusaEventWireMapping.TryParse(word, out PrinterEventType parsed).Should().BeTrue();
        parsed.Should().Be(value);
    }

    /// <summary>The same near misses, for the caller that drops a message rather than failing on it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("StorageInserted")]
    [InlineData("STORAGE_INSERTED")]
    [InlineData("medium_inserted")]
    [InlineData("CANCELLABLE_CHANGED")]
    [InlineData("UNDEFINED")]
    public void AWordOutsideTheVocabularyDoesNotTryParse(string word)
    {
        PrusaEventWireMapping.TryParse(word, out PrinterEventType parsed).Should().BeFalse();
        parsed.Should().Be(PrinterEventType.Undefined);
    }
}
