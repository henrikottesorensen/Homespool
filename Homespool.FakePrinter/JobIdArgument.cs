using System;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// The single <c>job_id</c> keyword argument that <c>SEND_JOB_INFO</c> carries - the only thing
/// firmware will answer a job question about.
/// </summary>
/// <remarks>
/// Its own parser rather than a generalised one, following <see cref="PathArgument"/>: each command
/// has its own required set, and a shared reader would have to make every field optional, which is
/// exactly the leniency firmware does not have.
/// </remarks>
public static class JobIdArgument
{
    /// <summary>Reads it out of a <c>J</c> frame payload, or null when it is missing or not a number.</summary>
    public static int? TryParse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("kwargs", out JsonElement kwargs)
                || kwargs.ValueKind != JsonValueKind.Object
                || !kwargs.TryGetProperty("job_id", out JsonElement jobId)
                || jobId.ValueKind != JsonValueKind.Number
                || !jobId.TryGetInt32(out int value))
            {
                return null;
            }

            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
