using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The unknown-field wrapper: it has to add properties the server does not model while leaving the
/// message a valid, otherwise-faithful telemetry document.
/// </summary>
/// <remarks>
/// The wrapper exists so <c>UnknownFieldTracker</c>'s 64-name cap and throttled summary can be
/// exercised at rate rather than only in unit tests - nothing in the fake could previously emit an
/// unmodelled field, so the server's <c>unknownFieldOccurrences</c> read 0 through every load run.
/// The two shapes are the two halves of that tracker's threat model: a handful of repeating names is
/// a firmware upgrade, a fresh name every message is an attacker.
/// </remarks>
public class UnknownFieldTelemetrySourceTests
{
    private sealed class FixedSource : ITelemetrySource
    {
        public string Json { get; init; } = """{"state":"IDLE"}""";

        public byte[]? NextMessage(FakeDevice device)
        {
            return System.Text.Encoding.UTF8.GetBytes(Json);
        }

        public TimeSpan DelayBeforeNext(FakeDevice device)
        {
            return TimeSpan.FromSeconds(1);
        }
    }

    private static JsonDocument Next(ITelemetrySource source)
    {
        return JsonDocument.Parse(source.NextMessage(new FakeDevice())!);
    }

    /// <summary>
    /// The added properties arrive, and the message is still valid JSON carrying what the inner
    /// source produced - the whole point is an <em>otherwise well-formed</em> message.
    /// </summary>
    [Fact]
    public void TheAddedFieldsRideOnAnOtherwiseIntactMessage()
    {
        UnknownFieldTelemetrySource source = new(new FixedSource()) { FieldsPerMessage = 3 };

        using JsonDocument document = Next(source);

        document.RootElement.GetProperty("state").GetString().Should().Be("IDLE",
            "the inner source's own fields must survive untouched");
        document.RootElement.EnumerateObject().Count(p => p.Name.StartsWith("unmodelled_", StringComparison.Ordinal))
            .Should().Be(3);
    }

    /// <summary>
    /// The default cycles a fixed set, so a long run produces a handful of distinct names however
    /// many messages it sends - the benign firmware-upgrade shape, which should be named once each
    /// and then leave the operator alone.
    /// </summary>
    [Fact]
    public void RepeatingModeReusesTheSameNames()
    {
        UnknownFieldTelemetrySource source = new(new FixedSource()) { FieldsPerMessage = 2 };
        HashSet<string> names = new(StringComparer.Ordinal);

        for (int i = 0; i < 50; i++)
        {
            using JsonDocument document = Next(source);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith("unmodelled_", StringComparison.Ordinal))
                {
                    names.Add(property.Name);
                }
            }
        }

        names.Should().HaveCount(2, "50 messages of the benign shape are still only two distinct names");
    }

    /// <summary>
    /// Distinct mode invents a fresh name every time, which is the only thing that drives the
    /// tracker's distinct-name cap and its past-the-cap summary.
    /// </summary>
    [Fact]
    public void DistinctModeNeverRepeatsAName()
    {
        UnknownFieldTelemetrySource source = new(new FixedSource()) { FieldsPerMessage = 2, Distinct = true };
        HashSet<string> names = new(StringComparer.Ordinal);

        for (int i = 0; i < 50; i++)
        {
            using JsonDocument document = Next(source);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith("unmodelled_", StringComparison.Ordinal))
                {
                    names.Add(property.Name);
                }
            }
        }

        names.Should().HaveCount(100, "every field of every message must be a name the server has not seen");
    }

    /// <summary>Zero fields is a pass-through, so the option is genuinely off unless asked for.</summary>
    [Fact]
    public void ZeroFieldsLeavesTheMessageAlone()
    {
        UnknownFieldTelemetrySource source = new(new FixedSource()) { FieldsPerMessage = 0 };

        using JsonDocument document = Next(source);

        document.RootElement.EnumerateObject().Should().HaveCount(1);
    }

    /// <summary>
    /// An empty object is passed through rather than turned into <c>{"x":0,}</c>. Sending malformed
    /// frames is <c>BrokenFrame</c>'s job; this wrapper must only ever add fields.
    /// </summary>
    [Fact]
    public void AnEmptyObjectIsNotCorruptedIntoInvalidJson()
    {
        UnknownFieldTelemetrySource source = new(new FixedSource { Json = "{}" }) { FieldsPerMessage = 2 };

        byte[] message = source.NextMessage(new FakeDevice())!;

        Action parse = () => JsonDocument.Parse(message).Dispose();

        parse.Should().NotThrow("a wrapper that adds unknown fields must not also invent invalid JSON");
    }

    /// <summary>
    /// A real telemetry message survives the splice with its firmware-derived field order intact -
    /// the added fields go in front rather than displacing anything.
    /// </summary>
    [Fact]
    public void ARealTelemetryMessageKeepsItsFieldOrder()
    {
        FakeDevice device = new();
        device.StartPrint(jobId: 7);

        byte[] original = TelemetryMessageBuilder.BuildFull(device, new TelemetryReadings());
        using JsonDocument before = JsonDocument.Parse(original);
        List<string> originalOrder = before.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        UnknownFieldTelemetrySource source = new(new FixedSource
        {
            Json = System.Text.Encoding.UTF8.GetString(original),
        })
        { FieldsPerMessage = 1 };

        using JsonDocument after = Next(source);
        List<string> spliced = after.RootElement.EnumerateObject().Select(p => p.Name)
            .Where(n => !n.StartsWith("unmodelled_", StringComparison.Ordinal)).ToList();

        spliced.Should().Equal(originalOrder,
            "field order came from a real capture, so the wrapper must not reorder what it wraps");
    }
}
