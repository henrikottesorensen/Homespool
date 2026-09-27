using System;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// The single <c>id</c> keyword argument <c>CANCEL_OBJECT</c> and <c>UNCANCEL_OBJECT</c> carry -
/// firmware parses it as a <c>uint16_t</c> (<c>command.cpp:408</c>).
/// </summary>
/// <remarks>
/// Its own parser rather than a generalised one, following <see cref="JobIdArgument"/>.
/// </remarks>
public static class ObjectIdArgument
{
    /// <summary>
    /// Reads it out of a <c>J</c> frame payload, or null when it is missing, not a number, or outside
    /// what a <c>uint16_t</c> holds.
    /// </summary>
    public static int? TryParse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("kwargs", out JsonElement kwargs) ||
                kwargs.ValueKind != JsonValueKind.Object ||
                !kwargs.TryGetProperty("id", out JsonElement id) ||
                id.ValueKind != JsonValueKind.Number ||
                !id.TryGetUInt16(out ushort value))
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
