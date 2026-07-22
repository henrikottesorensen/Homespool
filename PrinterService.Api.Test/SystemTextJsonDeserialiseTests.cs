using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.WebUtilities;

namespace PrinterService.Api.Test;

/// <summary>
/// Characterises how <c>System.Text.Json</c> copes with the two framings a Prusa printer actually
/// puts on the wire: objects concatenated with no separator at all, and objects separated by
/// newlines.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>System.Text.Json</c> half of a deliberate parser comparison — see
/// <see cref="JsonDotNetDeserialiseTests"/> for the same input through Json.NET. Unlike Json.NET,
/// there is no <c>SupportMultipleContent</c> switch here: multiple documents fall out of driving
/// <see cref="Utf8JsonReader"/> over a <see cref="PipeReader"/> and re-slicing the buffer by
/// <see cref="Utf8JsonReader.BytesConsumed"/> after each successful parse.
/// </para>
/// <para>
/// This mirrors what <c>WebSocketHandler</c> has to do for real, which is why the awkward-sized
/// reads matter: a WebSocket frame boundary has nothing to do with a JSON document boundary, so
/// values arrive split across reads.
/// </para>
/// </remarks>
public class SystemTextJsonDeserialiseTests
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
    public async Task MultiObjectJsonParses(string filename)
    {
        await using Stream file = File.OpenRead(filename);

        await AssertTelemetryObjectsAsync(file);
    }

    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]
    public async Task AwkwardSizedReadsParses(string filename)
    {
        // BufferedReadStream hands out data in AwkwardBufferSize-byte chunks, so documents are
        // guaranteed to straddle reads.
        await using Stream file = new BufferedReadStream(File.OpenRead(filename), AwkwardBufferSize);

        await AssertTelemetryObjectsAsync(file);
    }

    // Cannot be inline as the caller is async: Utf8JsonReader is a ref struct and so cannot live
    // across an await.
    private static bool TryParseJson(ref ReadOnlySequence<byte> buffer,
                                     bool isFinalBlock,
                                     [NotNullWhen(true)] out JsonDocument? jsonDocument)
    {
        Utf8JsonReader reader = new(buffer, isFinalBlock, default);

        if (JsonDocument.TryParseValue(ref reader, out jsonDocument))
        {
            buffer = buffer.Slice(reader.BytesConsumed);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Advances past inter-document whitespace, which is what separates the objects in the
    /// newline-delimited fixture and trails the last object in both.
    /// </summary>
    /// <remarks>
    /// This is not cosmetic. With <c>isFinalBlock: true</c>,
    /// <see cref="JsonDocument.TryParseValue"/> <b>throws</b> rather than returning false when
    /// the remaining input holds no JSON token — so a trailing newline after the final object is
    /// enough to blow up a reader that simply loops until the parse fails. Json.NET's
    /// <c>SupportMultipleContent</c> returns false in the same situation. Whitespace has to be
    /// consumed, and emptiness checked, <i>before</i> attempting a parse.
    /// </remarks>
    private static void AdvancePastWhitespace(ref ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> sequenceReader = new(buffer);

        sequenceReader.AdvancePastAny((byte)' ', (byte)'\t', (byte)'\r', (byte)'\n');

        buffer = buffer.Slice(sequenceReader.Position);
    }

    private static async IAsyncEnumerable<JsonDocument> ParseJsonDocument(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (true)
            {
                AdvancePastWhitespace(ref buffer);

                if (buffer.IsEmpty)
                {
                    break;
                }

                // isFinalBlock stays false while more data may still arrive: a document that
                // looks truncated now might simply be split across reads. Once the pipe reports
                // completion there is no more data coming, so a partial document is genuinely
                // malformed and the reader should say so rather than wait forever.
                if (!TryParseJson(ref buffer, result.IsCompleted, out JsonDocument? jsonDocument))
                {
                    break;
                }

                yield return jsonDocument;
            }

            // Required by the PipeReader contract. "Consumed" is where parsing stopped;
            // "examined" is the whole buffer, which tells the pipe that everything present has
            // been looked at and it must fetch more before the next read returns.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
    }

    private static async Task AssertTelemetryObjectsAsync(Stream stream)
    {
        List<JsonDocument> parsed = [];

        try
        {
            await foreach (JsonDocument jsonDocument in ParseJsonDocument(PipeReader.Create(stream)))
            {
                parsed.Add(jsonDocument);
            }

            // The count is the assertion that matters: it is what fails if the reader stops after
            // the first object, or silently drops the last one at end of stream.
            parsed.Should().HaveCount(ExpectedObjectCount);

            // Spot-check content as well as structure, so a run of empty documents cannot pass.
            parsed.Should().AllSatisfy(document =>
            {
                JsonElement root = document.RootElement;

                root.GetProperty("state").GetString().Should().Be("PRINTING");
                root.GetProperty("job_id").GetInt32().Should().Be(301);

                // Nested objects have to survive the same fragmented reads as the top level.
                root.GetProperty("chamber")
                    .GetProperty("led_intensity")
                    .GetInt32()
                    .Should()
                    .Be(100);
            });
        }
        finally
        {
            foreach (JsonDocument jsonDocument in parsed)
            {
                jsonDocument.Dispose();
            }
        }
    }
}
