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
}
