using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// What reaches a <c>PrinterEvent</c> row and what is lifted out of it - the <c>FILE_INFO</c>
/// allowlist, the drive listing, and the payload bound.
/// </summary>
/// <remarks>
/// <c>notes/printer-event-bounds.md</c> is the argument these pin. The short version: a directory
/// listing is a snapshot and the event log is append-only, so the listing goes to its own upserted
/// row and the event keeps only the fact that one arrived.
/// </remarks>
public sealed class PrusaTelemetryMappingPayloadTests
{
    private const string DirectoryListing = """
        {"type":"FOLDER","path":"/usb","file_count":2,"read_only":false,
         "children":[{"name":"A.BGC","display_name":"a.bgcode","size":17479245,"type":"PRINT_FILE"},
                     {"name":"B.BGC","display_name":"b.bgcode","size":22,"type":"PRINT_FILE"}]}
        """;

    private const string SingleFile = """
        {"type":"PRINT_FILE","path":"/usb/a.bgcode","display_name":"a.bgcode","size":17479245,
         "m_timestamp":1764616095,"read_only":false,"preview":"AAAA","estimated_print_time":900}
        """;

    private static PrinterEventRecord Map(string data, PrinterEventType type = PrinterEventType.FileInfo)
    {
        using JsonDocument parsed = JsonDocument.Parse(data);

        return PrusaTelemetryMapping.ToRecord(
            new EventDTO { EventType = type, Status = "IDLE", Data = parsed.RootElement.Clone() },
            identity: null);
    }

    /// <summary>
    /// <b>The listing does not reach the event row.</b> It is a snapshot, and this table appends -
    /// and because firmware puts print files in the drive root, one listing is the whole drive and
    /// only ever grows.
    /// </summary>
    [Fact]
    public void ADirectoryListingsChildrenAreNotStoredOnTheEvent()
    {
        PrinterEventRecord record = Map(DirectoryListing);

        record.Payload.Should().NotBeNull();
        record.Payload.Should().NotContain("children", "a snapshot does not belong in an append-only table");
        record.Payload.Should().NotContain("A.BGC");
    }

    /// <summary>
    /// <b>But the event still records that a listing arrived, and how big it was.</b> Dropping the
    /// count along with the entries would leave the occurrence unreadable.
    /// </summary>
    [Fact]
    public void ADirectoryListingKeepsItsFileCountOnTheEvent()
    {
        PrinterEventRecord record = Map(DirectoryListing);

        record.Payload.Should().Contain("file_count");
        record.Payload.Should().Contain("/usb", "the path is firmware-rendered and still wanted");
    }

    /// <summary>
    /// The entries are lifted out whole, with the printer's own count beside them.
    /// </summary>
    [Fact]
    public void ADirectoryListingBecomesADriveListingUpdate()
    {
        PrinterEventRecord record = Map(DirectoryListing);

        record.DriveListing.Should().NotBeNull();
        record.DriveListing!.FileCount.Should().Be(2);
        record.DriveListing.Entries.Should().Contain("A.BGC").And.Contain("B.BGC");
    }

    /// <summary>
    /// <b>The other shape under the same wire word produces no listing.</b> A single file's metadata
    /// is an occurrence, not a snapshot, and the queue consumes it as one - so treating it as a
    /// listing would overwrite the drive's contents with one file.
    /// </summary>
    [Fact]
    public void ASingleFileEventProducesNoDriveListing()
    {
        PrinterEventRecord record = Map(SingleFile);

        record.DriveListing.Should().BeNull();
    }

    /// <summary>
    /// The allowlist still does the job it was built for: a single file's gcode header goes.
    /// </summary>
    [Fact]
    public void ASingleFileEventStillLosesItsGcodeHeader()
    {
        PrinterEventRecord record = Map(SingleFile);

        record.Payload.Should().NotContain("preview", "pure gcode content, and the reason the allowlist exists");
        record.Payload.Should().NotContain("estimated_print_time");
        record.Payload.Should().Contain("display_name", "which the queue reads to match an arrival");
    }

    /// <summary>
    /// <b>An oversized payload is replaced, not cut short.</b> A JSON document sliced at a byte
    /// offset stops being JSON, and <c>QueueAdvancer</c> deserialises these.
    /// </summary>
    [Fact]
    public void AnOversizedPayloadBecomesAMarker()
    {
        string huge = $$"""{"type":"PRINT_FILE","display_name":"{{new string('x', 8192)}}"}""";

        PrinterEventRecord record = Map(huge);

        record.Payload.Should().NotBeNull();
        record.Payload!.Length.Should().BeLessThan(200, "the marker is small whatever it replaced");
        record.Payload.Should().Contain("_truncated");
    }

    /// <summary>
    /// <b>And the marker is still JSON the queue can read.</b> This is the property that makes
    /// replacement better than truncation: the reader gets a record with nothing set rather than a
    /// parse failure it cannot tell from a printer sending something unmodelled.
    /// </summary>
    [Fact]
    public void TheMarkerRemainsDeserialisableByTheQueuesReader()
    {
        string huge = $$"""{"type":"PRINT_FILE","display_name":"{{new string('x', 8192)}}"}""";

        PrinterEventRecord record = Map(huge);

        FileInfoEventDataDTO? read = JsonSerializer.Deserialize<FileInfoEventDataDTO>(record.Payload!);

        read.Should().NotBeNull();
        read!.DisplayName.Should().BeNull("nothing survived, and the reader skips on a null name");
    }

    /// <summary>
    /// An ordinary payload passes through untouched - the bound refuses the outsized, not everything.
    /// </summary>
    [Fact]
    public void AnOrdinaryPayloadIsUnchanged()
    {
        PrinterEventRecord record = Map(SingleFile);

        record.Payload.Should().NotContain("_truncated");
        record.Payload.Should().Contain("a.bgcode");
    }
}
