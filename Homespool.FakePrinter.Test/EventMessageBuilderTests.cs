using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// Event JSON against the firmware's renderer (render.cpp:268-679): field order, <c>state</c>
/// always present, <c>event</c> last, and the INFO data block's contents.
/// </summary>
public class EventMessageBuilderTests
{
    /// <summary>
    /// A rejection renders reason before state, command id after it, and the event name last -
    /// the firmware's exact ordering.
    /// </summary>
    [Fact]
    public void ARejectionRendersTheFirmwareFieldOrder()
    {
        byte[] message = EventMessageBuilder.Build("REJECTED", "IDLE", 5, "No print to pause");

        using JsonDocument document = JsonDocument.Parse(message);
        List<string> names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().Equal("reason", "state", "command_id", "event");
        document.RootElement.GetProperty("reason").GetString().Should().Be("No print to pause");
        document.RootElement.GetProperty("state").GetString().Should().Be("IDLE");
        document.RootElement.GetProperty("command_id").GetUInt32().Should().Be(5);
        document.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
    }

    /// <summary>A FINISHED with a job carries job_id first; state is always present.</summary>
    [Fact]
    public void AFinishedWithAJobCarriesJobIdFirst()
    {
        byte[] message = EventMessageBuilder.Build("FINISHED", "PAUSED", 7, jobId: 301);

        using JsonDocument document = JsonDocument.Parse(message);
        List<string> names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().Equal("job_id", "state", "command_id", "event");
        document.RootElement.GetProperty("job_id").GetInt32().Should().Be(301);
    }

    /// <summary>
    /// The INFO data block carries the full 50-character fingerprint and the serial - the one
    /// place either appears on <c>/p/ws</c> (render.cpp:344, confirmed by the capture).
    /// </summary>
    [Fact]
    public void InfoCarriesTheFullFingerprintAndSerial()
    {
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        byte[] message = EventMessageBuilder.BuildInfo(identity, "IDLE", 320);

        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement data = document.RootElement.GetProperty("data");

        data.GetProperty("fingerprint").GetString().Should().Be(identity.Fingerprint);
        data.GetProperty("sn").GetString().Should().Be(identity.SerialNumber);
        data.GetProperty("firmware").GetString().Should().Be(identity.Firmware);
        data.GetProperty("printer_type").GetString().Should().Be(identity.PrinterType);
        data.GetProperty("tools").GetProperty("1").GetProperty("material").GetString().Should().Be("PLA");

        // The footer still applies: data before state, event last.
        List<string> names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().Equal("data", "state", "command_id", "event");
        document.RootElement.GetProperty("event").GetString().Should().Be("INFO");
    }

    /// <summary>Without a command id (the connect-time INFO) the field is simply absent.</summary>
    [Fact]
    public void InfoWithoutACommandIdOmitsTheField()
    {
        byte[] message = EventMessageBuilder.BuildInfo(PrinterIdentity.CreateRandom(), "IDLE");

        using JsonDocument document = JsonDocument.Parse(message);

        document.RootElement.TryGetProperty("command_id", out _).Should().BeFalse();
    }
}
