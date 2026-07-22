using AwesomeAssertions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PrinterService.Api.Test;

/// <summary>
/// Characterises how Json.NET copes with the two framings a Prusa printer actually puts on the
/// wire: objects concatenated with no separator at all, and objects separated by newlines.
/// </summary>
/// <remarks>
/// <para>
/// This is the Json.NET half of a deliberate parser comparison — see
/// <see cref="SystemTextJsonDeserialiseTests"/> for the same input through
/// <c>System.Text.Json</c>. The point is that a WebSocket frame boundary has nothing to do with
/// a JSON document boundary, so whatever we use has to keep reading past the closing brace of
/// the first object and pick up the next one.
/// </para>
/// <para>
/// Both fixtures contain <see cref="ExpectedObjectCount"/> telemetry messages. Asserting that
/// count is the whole point: an assertion made only <i>inside</i> the read loop passes happily
/// when the reader stops early, which is precisely the failure being guarded against.
/// </para>
/// </remarks>
public class JsonDotNetDeserialiseTests
{
    /// <summary>Number of telemetry objects in each of the fixture files.</summary>
    private const int ExpectedObjectCount = 4;

    /// <summary>
    /// Deliberately tiny, and not a multiple of anything meaningful, so that reads land in the
    /// middle of tokens and values rather than tidily on object boundaries.
    /// </summary>
    private const int AwkwardBufferSize = 3;

    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]
    public void MultiObjectJsonParses(string filename)
    {
        using Stream file = File.OpenRead(filename);

        List<JObject> parsed = ReadAll(file);

        AssertTelemetryObjects(parsed);
    }

    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]
    public void AwkwardSizedReadsParses(string filename)
    {
        using Stream file = File.OpenRead(filename);
        using BufferedStream bufferedStream = new(file, AwkwardBufferSize);

        List<JObject> parsed = ReadAll(bufferedStream);

        AssertTelemetryObjects(parsed);
    }

    /// <summary>
    /// Reads every JSON object from <paramref name="stream"/>, relying on
    /// <see cref="JsonTextReader.SupportMultipleContent"/> to continue past the end of each one.
    /// </summary>
    private static List<JObject> ReadAll(Stream stream)
    {
        using JsonTextReader reader = new(new StreamReader(stream))
        {
            SupportMultipleContent = true,
        };

        JsonSerializer serializer = new();

        List<JObject> parsed = [];

        while (reader.Read())
        {
            JObject? o = serializer.Deserialize<JObject>(reader);

            o.Should().NotBeNull("every top-level value in the fixtures is an object");

            parsed.Add(o);
        }

        return parsed;
    }

    private static void AssertTelemetryObjects(List<JObject> parsed)
    {
        // The count is the assertion that matters: it is what fails if the reader stops after
        // the first object instead of continuing through the stream.
        parsed.Should().HaveCount(ExpectedObjectCount);

        // Spot-check content as well as structure, so a run of empty objects cannot pass.
        parsed.Should().AllSatisfy(o =>
        {
            o.Value<string>("state").Should().Be("PRINTING");
            o.Value<int?>("job_id").Should().Be(301);

            // Nested objects have to survive the same fragmented reads as the top level.
            o["chamber"].Should().NotBeNull();
            o["chamber"]!.Value<int?>("led_intensity").Should().Be(100);
        });
    }
}
