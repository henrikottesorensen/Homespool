using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Lifting a <c>CANCELABLE_CHANGED</c> off the wire into the plate's live state, and the stored form
/// of the cancelled ids.
/// </summary>
/// <remarks>
/// The cases that matter are the ones where being wrong is silent: an object read as not cancelled
/// when it is, or a report that is partly read and stored as though it were the whole.
/// </remarks>
public class CancellableObjectsMappingTests
{
    private static PrinterEventRecord Map(string data, PrinterEventType type = PrinterEventType.CancelableChanged)
    {
        using JsonDocument parsed = JsonDocument.Parse(data);

        return PrusaTelemetryMapping.ToRecord(
            new EventDTO { EventType = type, Status = "PRINTING", Data = parsed.RootElement.Clone() },
            identity: null);
    }

    /// <summary>
    /// The report captured from hardware after A, D and E were cancelled on the six-cube plate:
    /// the count is the entries, and the cancelled ids are the ones flagged.
    /// </summary>
    [Fact]
    public void AReportIsLiftedAsTheCountAndTheCancelledIds()
    {
        PrinterEventRecord record = Map(
            """
            {"objects":[{"canceled":false,"id":0},{"canceled":false,"id":1},{"canceled":true,"id":2},
                        {"canceled":false,"id":3},{"canceled":true,"id":4},{"canceled":true,"id":5}]}
            """);

        record.Cancellable.Should().NotBeNull();
        record.Cancellable!.ObjectCount.Should().Be(6);
        record.Cancellable.CancelledIds.Should().BeEquivalentTo([2, 4, 5]);
    }

    /// <summary>
    /// The empty report firmware sends outside a print is an update - zero objects - and not the
    /// absence of one, because it is what clears the plate when a print ends.
    /// </summary>
    [Fact]
    public void AnEmptyReportIsAnUpdateToNothing()
    {
        PrinterEventRecord record = Map("""{"objects":[]}""");

        record.Cancellable.Should().NotBeNull();
        record.Cancellable!.ObjectCount.Should().Be(0);
        record.Cancellable.CancelledIds.Should().BeEmpty();
    }

    /// <summary>
    /// Only this event says anything about the plate. Any other carrying an update would replace the
    /// stored plate with whatever it happened to parse as.
    /// </summary>
    [Theory]
    [InlineData(PrinterEventType.StateChanged)]
    [InlineData(PrinterEventType.Finished)]
    [InlineData(PrinterEventType.JobInfo)]
    [InlineData(PrinterEventType.FileInfo)]
    public void OnlyACancellableReportCarriesAPlate(PrinterEventType type)
    {
        Map("""{"objects":[{"canceled":true,"id":0}]}""", type).Cancellable.Should().BeNull();
    }

    /// <summary>
    /// <b>A report that will not read whole is not read at all.</b> Storing the parts that parse
    /// would describe a plate the printer never reported, and could show a cancelled object as still
    /// printing; a null keeps whatever was stored before.
    /// </summary>
    [Theory]
    [InlineData("""{"objects":[{"canceled":true,"id":0},{"canceled":"yes","id":1}]}""")]
    [InlineData("""{"objects":[{"canceled":true,"id":0},{"canceled":false}]}""")]
    [InlineData("""{"objects":[{"canceled":true,"id":"0"}]}""")]
    [InlineData("""{"objects":[{"canceled":true,"id":2}]}""")]
    [InlineData("""{"objects":[{"canceled":true,"id":-1}]}""")]
    [InlineData("""{"objects":[{"canceled":true,"id":0.5}]}""")]
    [InlineData("""{"objects":[7]}""")]
    [InlineData("""{"objects":{}}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public void AReportThatWillNotReadWholeIsNotRead(string data)
    {
        Map(data).Cancellable.Should().BeNull();
    }

    /// <summary>
    /// More objects than firmware can declare is not something firmware sends, and is not read: the
    /// count bounds what one printer's token can make this server store and render.
    /// </summary>
    [Fact]
    public void AReportPastFirmwaresCeilingIsNotRead()
    {
        int count = PrusaConnectConstants.MaxCancellableObjects + 1;
        string objects = string.Join(',', Enumerable.Range(0, count).Select(id => $$"""{"canceled":false,"id":{{id}}}"""));

        Map($$"""{"objects":[{{objects}}]}""").Cancellable.Should().BeNull();
    }

    /// <summary>
    /// The stored form is ascending and duplicate-free whatever order the ids came in, so an
    /// unchanged plate renders byte-identically and the polled region is left alone.
    /// </summary>
    [Fact]
    public void TheStoredFormIsAscendingAndDistinct()
    {
        CancelledObjects.Format([5, 2, 4, 2]).Should().Be("2,4,5");
        CancelledObjects.Format([]).Should().BeEmpty();
    }

    /// <summary>What is formatted parses back to the same set.</summary>
    [Fact]
    public void TheStoredFormRoundTrips()
    {
        CancelledObjects.Parse(CancelledObjects.Format([0, 3, 1023])).Should().BeEquivalentTo([0, 3, 1023]);
    }

    /// <summary>
    /// A stored value this code did not write is read for what can be read rather than thrown over,
    /// because it is read on a page render.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingStoredIsNoneCancelled(string? stored)
    {
        CancelledObjects.Parse(stored).Should().BeEmpty();
    }

    /// <summary>Anything that is not a plain non-negative number is dropped.</summary>
    [Fact]
    public void AnUnreadablePartIsDropped()
    {
        CancelledObjects.Parse("2,x,-1,+3, 4,5").Should().BeEquivalentTo([2, 5]);
    }
}
