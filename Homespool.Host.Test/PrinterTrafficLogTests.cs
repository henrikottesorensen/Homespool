using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterTrafficLog"/> - the one place message bodies are written to disk, and therefore
/// the one place a printer credential could be written to disk by accident.
/// </summary>
/// <remarks>
/// The load-bearing tests here are <see cref="ARotatedTokenIsNeverWritten"/> and
/// <see cref="AnEncryptionKeyIsNeverWritten"/>. Everything else fails noisily when it regresses;
/// those two guard the property whose violation looks like the feature working - a fuller record of
/// what went over the wire - and would put a printer's replacement credential and a transfer's AES
/// key in the file an operator is most likely to attach to a bug report.
/// </remarks>
public sealed class PrinterTrafficLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "homespool-traffic-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A traffic log that records nothing, for the several fixtures that have one injected and do
    /// not care about it.
    /// </summary>
    /// <remarks>
    /// Static and shared: a disabled log holds no file handle and no state, so there is nothing for
    /// each fixture to own or dispose - and a local would oblige every one of them to grow disposal
    /// ceremony for an object that does nothing.
    /// </remarks>
    public static readonly PrinterTrafficLog Off =
        new(Options.Create(new PrinterTrafficLogOptions()), NullLogger<PrinterTrafficLog>.Instance);

    private PrinterTrafficLog NewLog(bool telemetry = false)
    {
        return new PrinterTrafficLog(
            Options.Create(new PrinterTrafficLogOptions
            {
                Enabled = true,
                Telemetry = telemetry,
                Path = Path.Combine(_directory, "traffic-.jsonl"),
            }),
            NullLogger<PrinterTrafficLog>.Instance);
    }

    /// <summary>
    /// The written records, parsed. Reads after the sink is disposed, because the sink owns the file
    /// handle until then.
    /// </summary>
    private List<JsonElement> Records()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_directory)
                        .SelectMany(File.ReadAllLines)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                        .ToList();
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void OffByDefaultNothingIsWritten()
    {
        // Arrange
        PrinterTrafficLog log = Off;

        // Act
        log.RecordInbound(1, Parse("""{"event": "STATE_CHANGED"}"""));
        log.RecordOutbound(1, 7, new PausePrint());

        // Assert
        log.IsEnabled.Should().BeFalse();
        Records().Should().BeEmpty();
    }

    [Fact]
    public void AnInboundEventIsRecordedWithItsBody()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordInbound(4, Parse("""{"event": "JOB_INFO", "job_id": 736, "state": "PRINTING"}"""));
        log.Dispose();

        // Assert
        JsonElement record = Records().Should().ContainSingle().Subject;

        record.GetProperty("dir").GetString().Should().Be("p2s");
        record.GetProperty("printer").GetInt32().Should().Be(4);
        record.GetProperty("json").GetProperty("event").GetString().Should().Be("JOB_INFO");
        record.GetProperty("json").GetProperty("job_id").GetInt32().Should().Be(736);
    }

    [Fact]
    public void AnOutboundCommandIsRecordedWithItsArguments()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordOutbound(4, 12, new StartPrint { Path = "/usb/BENCHY.BGC" });
        log.Dispose();

        // Assert
        JsonElement record = Records().Should().ContainSingle().Subject;

        record.GetProperty("dir").GetString().Should().Be("s2p");
        record.GetProperty("command_id").GetUInt32().Should().Be(12);
        record.GetProperty("json").GetProperty("command").GetString().Should().Be("START_PRINT");
        record.GetProperty("json").GetProperty("kwargs").GetProperty("path").GetString()
              .Should().Be("/usb/BENCHY.BGC");
    }

    /// <summary>
    /// The volume switch. Telemetry is roughly 1 Hz per printer and is nearly all of the bytes, so
    /// leaving it out is what makes the log runnable for days on an appliance writing to an SD card.
    /// </summary>
    [Fact]
    public void TelemetryIsExcludedUnlessAskedFor()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordInbound(1, Parse("""{"state": "PRINTING", "temp_nozzle": 215}"""));
        log.RecordInbound(1, Parse("""{"event": "STATE_CHANGED"}"""));
        log.Dispose();

        // Assert - the event survived, so this is a filter rather than a broken sink
        Records().Should().ContainSingle()
                 .Which.GetProperty("json").GetProperty("event").GetString().Should().Be("STATE_CHANGED");
    }

    [Fact]
    public void TelemetryIsRecordedWhenAskedFor()
    {
        // Arrange
        PrinterTrafficLog log = NewLog(telemetry: true);

        // Act
        log.RecordInbound(1, Parse("""{"state": "PRINTING", "temp_nozzle": 215}"""));
        log.Dispose();

        // Assert
        Records().Should().ContainSingle()
                 .Which.GetProperty("json").GetProperty("temp_nozzle").GetInt32().Should().Be(215);
    }

    /// <summary>
    /// An <c>INFO</c> event carries the printer's <c>fingerprint</c>, its serial, and an
    /// <c>api_key</c> that is the PrusaLink password - nested under <c>data</c>, which is why this
    /// asserts at depth rather than on the envelope.
    /// </summary>
    [Fact]
    public void InboundSecretsAreRedactedAtAnyDepth()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordInbound(1, Parse("""
            {
                "event": "INFO",
                "data": { "sn": "SERIAL9", "fingerprint": "FINGER9", "api_key": "PASSWORD9",
                          "nozzle_diameter": 0.4 }
            }
            """));
        log.Dispose();

        // Assert
        string written = string.Join('\n', Records().Select(record => record.GetRawText()));

        written.Should().NotContain("SERIAL9");
        written.Should().NotContain("FINGER9");
        written.Should().NotContain("PASSWORD9");

        // The message is still worth reading - redaction that removed the event would be a leak
        // fixed by deleting the evidence.
        written.Should().Contain("INFO");
        written.Should().Contain("nozzle_diameter");
    }

    /// <summary>
    /// The same rule as the actor's <c>ARotatedTokenIsNeverLogged</c>, against the file that
    /// deliberately does record arguments. <c>SET_TOKEN</c> is a printer's replacement credential,
    /// issued precisely when one is believed compromised.
    /// </summary>
    /// <remarks>
    /// Asserts both halves. Without the first, a change that silently stopped recording outbound
    /// commands at all would pass this and prove nothing.
    /// </remarks>
    [Fact]
    public void ARotatedTokenIsNeverWritten()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordOutbound(1, 3, new SetToken { Token = "replacement-token99" });
        log.Dispose();

        // Assert
        string written = Records().Should().ContainSingle().Subject.GetRawText();

        written.Should().Contain("SET_TOKEN", "the command has to be in the record, or this proves nothing");
        written.Should().NotContain("replacement-token99");
    }

    /// <summary>
    /// The transfer cipher's key and IV travel as command arguments, and a record holding both is a
    /// record that decrypts the file.
    /// </summary>
    /// <remarks>
    /// <b>Asserted against the hex spelling, which is the one that goes on the wire</b> - the command
    /// hands firmware <c>Convert.ToHexStringLower</c>, not the raw bytes. Written against base64
    /// first, which made this pass whatever the code did: dropping redaction entirely left it green,
    /// because the string it looked for was never going to be in the file. The same shape as the
    /// hollow <c>SetToken</c> marker that made an earlier version of the actor's own token test
    /// unfalsifiable.
    /// </remarks>
    [Fact]
    public void AnEncryptionKeyIsNeverWritten()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();
        byte[] key = Enumerable.Repeat((byte)0xAB, StartEncryptedDownload.KeyLength).ToArray();
        byte[] iv = Enumerable.Repeat((byte)0xCD, StartEncryptedDownload.IvLength).ToArray();

        // Act
        log.RecordOutbound(1, 4, new StartEncryptedDownload
        {
            Path = "/usb/BENCHY.BGC",
            Key = key,
            Iv = iv,
        });

        log.Dispose();

        // Assert
        string written = Records().Should().ContainSingle().Subject.GetRawText();

        written.Should().Contain(StartEncryptedDownload.Wire, "the command has to be in the record, or this proves nothing");
        written.Should().Contain("/usb/BENCHY.BGC");
        written.Should().NotContain(Convert.ToHexStringLower(key));
        written.Should().NotContain(Convert.ToHexStringLower(iv));
    }

    /// <summary>
    /// The measured shape this bound exists for: a <c>FILE_INFO</c> whose <c>preview</c> is an 89 KB
    /// base64 PNG, in a 92 831-byte message. Without this a handful of them is the whole file.
    /// </summary>
    [Fact]
    public void ALongStringIsElidedRatherThanWritten()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();
        string preview = new('A', 20_000);

        // Act
        log.RecordInbound(1, Parse($$"""{"event": "FILE_INFO", "preview": "{{preview}}"}"""));
        log.Dispose();

        // Assert
        JsonElement record = Records().Should().ContainSingle().Subject;

        record.GetProperty("json").GetProperty("preview").GetString()
              .Should().Be("<20000 characters elided>");

        // Short values in the same message are untouched - this is a size bound, not a field filter.
        record.GetProperty("json").GetProperty("event").GetString().Should().Be("FILE_INFO");
    }

    /// <summary>
    /// One JSON object per line, not a log file with JSON in it. Pins the sink's output template:
    /// give it a conventional one - a timestamp, a level, then the message - and every record gains
    /// a prefix that stops it parsing, which is what makes this a format rather than a log.
    /// </summary>
    /// <remarks>
    /// Parses each line rather than checking its first character. The weaker form passed against a
    /// template mutation that a decoder would have choked on.
    /// </remarks>
    [Fact]
    public void EachRecordIsOneUnescapedJsonObjectPerLine()
    {
        // Arrange
        PrinterTrafficLog log = NewLog();

        // Act
        log.RecordInbound(1, Parse("""{"event": "STATE_CHANGED"}"""));
        log.RecordOutbound(1, 1, new PausePrint());
        log.Dispose();

        // Assert
        string[] lines = Directory.EnumerateFiles(_directory)
                                  .SelectMany(File.ReadAllLines)
                                  .Where(line => !string.IsNullOrWhiteSpace(line))
                                  .ToArray();

        lines.Should().HaveCount(2);
        lines.Should().AllSatisfy(line =>
            JsonDocument.Parse(line).RootElement.ValueKind.Should().Be(
                JsonValueKind.Object,
                "a record rendered as a quoted, escaped string parses as a JSON string and no decoder reads it"));
    }

    /// <summary>
    /// The ordering the dispatcher's call site encodes: a message is recorded before anything tries
    /// to deserialize it, so the one message with the least other evidence behind it - the one that
    /// costs the connection and leaves only an exception - is in the file.
    /// </summary>
    /// <remarks>
    /// This is the mutation guard for moving the <c>RecordInbound</c> call below the parse in
    /// <see cref="MessageDispatcher.Classify"/>, which is what a tidying edit would do.
    /// </remarks>
    [Fact]
    public void AMessageThatCannotBeDeserializedIsStillRecorded()
    {
        // Arrange
        PrinterTrafficLog log = NewLog(telemetry: true);
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance,
                                           new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                                           TimeProvider.System,
                                           log);

        // Act - telemetry without the required "state", which throws on the way to a TelemetryDTO
        Action classify = () => dispatcher.Classify(1, Parse("""{"job_id": 736}"""));

        classify.Should().Throw<JsonException>();

        log.Dispose();

        // Assert
        Records().Should().ContainSingle()
                 .Which.GetProperty("json").GetProperty("job_id").GetInt32().Should().Be(736);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
