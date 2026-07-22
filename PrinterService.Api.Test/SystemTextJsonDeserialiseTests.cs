using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

using Xunit.Abstractions;

namespace PrinterService.Api.Test;

public class SystemTextJsonDeserialiseTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public SystemTextJsonDeserialiseTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    // Cannot be inline as the method is async, so Utf8JsonReader cannot be instantiated (ref struct)
    private static bool TryParseJson(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out JsonDocument? jsonDocument)
    {
        Utf8JsonReader reader = new(buffer, isFinalBlock: false, default);

        if (JsonDocument.TryParseValue(ref reader, out jsonDocument))
        {
            buffer = buffer.Slice(reader.BytesConsumed);
            return true;
        }

        return false;
    }

    private static async IAsyncEnumerable<JsonDocument> ParseJsonDocument(PipeReader reader)
    {
        ReadResult result = await reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            if (TryParseJson(ref buffer, out JsonDocument? jsonDocument))
            {
                yield return jsonDocument;
            }
        }
    }
    
    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]

    public async Task MultiObjectJsonParses(string filename)
    {
        using Stream file = new BufferedReadStream(File.OpenRead(filename), 3);

        int i = 0;
        PipeReader pipeReader = PipeReader.Create(file);
        await foreach (JsonDocument jsonDocument in ParseJsonDocument(pipeReader))
        {
            _testOutputHelper.WriteLine(JsonSerializer.Serialize(jsonDocument.RootElement));
            ++i;
        }
    }
}
