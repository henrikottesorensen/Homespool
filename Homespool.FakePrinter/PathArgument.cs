using System;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// The single <c>path</c> keyword argument that <c>SEND_FILE_INFO</c> carries
/// (Prusa-Firmware-Buddy <c>planner.cpp:751-759</c> at the pinned ref).
/// </summary>
/// <remarks>
/// Separate from <see cref="StartDownloadArguments"/> rather than folded into it: they are different
/// commands with different required sets, and a shared parser would have to make <c>hash</c> and
/// <c>orig_size</c> optional, which is exactly the leniency firmware does not have.
/// </remarks>
public static class PathArgument
{
    /// <summary>Reads it out of a <c>J</c> frame payload, or null when it is missing or not a string.</summary>
    public static string? TryParse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("kwargs", out JsonElement kwargs)
                || kwargs.ValueKind != JsonValueKind.Object
                || !kwargs.TryGetProperty("path", out JsonElement path)
                || path.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return path.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
