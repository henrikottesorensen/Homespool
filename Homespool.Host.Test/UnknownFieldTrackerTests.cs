using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="UnknownFieldTracker"/> and its wiring through <see cref="MessageDispatcher"/> - the
/// mechanism that makes a printer sending something this build does not model visible instead of
/// silent.
/// </summary>
/// <remarks>
/// The load-bearing test here is <see cref="EventDataKeysAreNeverWalked"/>. Everything else fails
/// noisily if it regresses; that one guards a property whose violation would look like the feature
/// working harder - and would write hundreds of attacker-chosen key names into the log.
/// </remarks>
public class UnknownFieldTrackerTests
{
    private static (MessageDispatcher dispatcher, UnknownFieldTracker tracker) NewDispatcher()
    {
        UnknownFieldTracker tracker = new(NullLogger<UnknownFieldTracker>.Instance);

        return (new MessageDispatcher(NullLogger<MessageDispatcher>.Instance, tracker, TimeProvider.System), tracker);
    }

    private static void Classify(MessageDispatcher dispatcher, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        dispatcher.Classify(1, document.RootElement);
    }

    // ---------- The tracker itself ----------
    [Fact]
    public void NothingUnmatchedRecordsNothing()
    {
        // Arrange
        FakeLogger<UnknownFieldTracker> logger = new();
        UnknownFieldTracker tracker = new(logger);

        // Act
        tracker.Record(1, "telemetry", null);
        tracker.Record(1, "telemetry", []);

        // Assert
        tracker.Total.Should().Be(0);
        tracker.DistinctFields.Should().BeEmpty();
        logger.Collector.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void FirstSightingOfADistinctFieldLogsOnceButEveryOccurrenceCounts()
    {
        // Arrange
        FakeLogger<UnknownFieldTracker> logger = new();
        UnknownFieldTracker tracker = new(logger);
        Dictionary<string, JsonElement> unknown = Unknown("""{"brand_new":1}""");

        // Act - the same field arriving three times, as a 1 Hz telemetry field would
        tracker.Record(1, "telemetry", unknown);
        tracker.Record(1, "telemetry", unknown);
        tracker.Record(1, "telemetry", unknown);

        // Assert
        tracker.Total.Should().Be(3, "occurrences are counted exactly even though logging is bounded");
        tracker.DistinctFields.Should().ContainSingle().Which.Should().Be("telemetry.brand_new");
        logger.Collector.GetSnapshot().Should().ContainSingle()
              .Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void TheShapeQualifiesTheFieldName()
    {
        // Arrange
        UnknownFieldTracker tracker = new(NullLogger<UnknownFieldTracker>.Instance);
        Dictionary<string, JsonElement> unknown = Unknown("""{"mystery":1}""");

        // Act - the same key name from two different places on the wire
        tracker.Record(1, "telemetry", unknown);
        tracker.Record(1, "event:FileInfo", unknown);

        // Assert
        tracker.DistinctFields.Should().BeEquivalentTo(["event:FileInfo.mystery", "telemetry.mystery"]);
    }

    /// <summary>
    /// The rule from <c>0a6ae53</c>, applied to a new surface: an unknown field's <i>value</i> is
    /// exactly as likely to be file content as the gcode that commit stripped out of the event log.
    /// Only the key name and the <see cref="JsonValueKind"/> may be recorded.
    /// </summary>
    [Fact]
    public void ValuesAreNeverLogged()
    {
        // Arrange
        FakeLogger<UnknownFieldTracker> logger = new();
        UnknownFieldTracker tracker = new(logger);

        // Act
        tracker.Record(1, "telemetry", Unknown("""{"surprise":"SENSITIVE-PAYLOAD-CONTENT"}"""));

        // Assert
        FakeLogRecord record = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;

        record.Message.Should().Contain("telemetry.surprise")
              .And.Contain("String", "the JSON kind is the useful, safe half of the value");
        record.Message.Should().NotContain("SENSITIVE-PAYLOAD-CONTENT");
        record.StructuredState.Should().NotContain(pair => pair.Value != null && pair.Value.Contains("SENSITIVE-PAYLOAD-CONTENT", StringComparison.Ordinal));
    }

    [Fact]
    public void DistinctFieldsAreCappedWhileOccurrencesKeepCounting()
    {
        // Arrange
        FakeLogger<UnknownFieldTracker> logger = new();
        UnknownFieldTracker tracker = new(logger);
        int flood = UnknownFieldTracker.MaxDistinctFields * 2;

        // Act - what a client sending junk keys at wire rate looks like
        for (int i = 0; i < flood; i++)
        {
            tracker.Record(1, "telemetry", Unknown($$"""{"junk_{{i}}":1}"""));
        }

        // Assert
        tracker.DistinctFields.Should().HaveCount(UnknownFieldTracker.MaxDistinctFields);
        tracker.Total.Should().Be(flood, "the cap bounds memory and logging, never the count");

        // One line per learned name, plus at most one throttled "past the cap" summary. The point is
        // that it is bounded by the cap rather than by the flood.
        logger.Collector.GetSnapshot().Should().HaveCountLessThanOrEqualTo(UnknownFieldTracker.MaxDistinctFields + 1);
    }

    // ---------- Wiring through the dispatcher ----------
    [Fact]
    public void AFullyModelledTelemetryMessageRecordsNothing()
    {
        // Arrange
        (MessageDispatcher dispatcher, UnknownFieldTracker tracker) = NewDispatcher();

        // Act
        Classify(dispatcher, """{"state":"PRINTING","temp_nozzle":215.0,"temp_bed":60.0,"progress":42}""");

        // Assert - also the evidence that System.Text.Json leaves the extension property null rather
        // than allocating an empty dictionary on every message of the ordinary 1 Hz path.
        tracker.Total.Should().Be(0);
    }

    [Fact]
    public void UnmodelledTelemetryFieldsAreRecorded()
    {
        // Arrange
        (MessageDispatcher dispatcher, UnknownFieldTracker tracker) = NewDispatcher();

        // Act
        Classify(dispatcher, """{"state":"PRINTING","temp_nozzle":215.0,"temp_chamber_lid":31.5}""");

        // Assert
        tracker.DistinctFields.Should().ContainSingle().Which.Should().Be("telemetry.temp_chamber_lid");
    }

    [Fact]
    public void UnmodelledNestedTelemetryFieldsAreRecordedUnderTheirOwnShape()
    {
        // Arrange
        (MessageDispatcher dispatcher, UnknownFieldTracker tracker) = NewDispatcher();

        // Act
        Classify(dispatcher, """{"state":"PRINTING","chamber":{"temp":25.0,"humidity":40}}""");

        // Assert
        tracker.DistinctFields.Should().ContainSingle().Which.Should().Be("telemetry.chamber.humidity");
    }

    [Fact]
    public void UnmodelledEventEnvelopeFieldsAreQualifiedByEventType()
    {
        // Arrange
        (MessageDispatcher dispatcher, UnknownFieldTracker tracker) = NewDispatcher();

        // Act
        Classify(dispatcher, """{"event":"STATE_CHANGED","state":"PRINTING","severity":"warning"}""");

        // Assert
        tracker.DistinctFields.Should().ContainSingle().Which.Should().Be("event:StateChanged.severity");
    }

    /// <summary>
    /// <b>The one that matters.</b> A <c>FILE_INFO</c>'s <c>data</c> is the uploaded gcode's own
    /// header - 396 of 407 keys in a measured real transfer, with names the <i>file</i> chose
    /// (<c>8cf4cdb</c>). Walking into it would turn this feature into an unbounded, attacker-named
    /// log writer and blow the distinct cap on a single ordinary print.
    /// </summary>
    [Fact]
    public void EventDataKeysAreNeverWalked()
    {
        // Arrange
        (MessageDispatcher dispatcher, UnknownFieldTracker tracker) = NewDispatcher();

        // Act - an envelope this build models exactly, wrapping a payload full of keys it does not
        Classify(dispatcher, """
            {"event":"FILE_INFO","state":"IDLE","command_id":7,
             "data":{"path":"/usb/thing.bkp","size":12,
                     "iP5nSy8PNRc1GwE6w/9UVFSAPBk4":"thumbnail-fragment",
                     "arbitrary_slicer_key":"whatever","another_one":1}}
            """);

        // Assert
        tracker.Total.Should().Be(0, "data is raw JsonElement and must never be walked");
        tracker.DistinctFields.Should().BeEmpty();
    }

    private static Dictionary<string, JsonElement> Unknown(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }
}
