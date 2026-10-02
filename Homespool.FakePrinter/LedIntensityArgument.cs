using System;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// The <c>chamber.led_intensity</c> keyword argument of <c>SET_VALUE</c> - firmware parses it as an
/// <c>int8_t</c> (<c>command.cpp:419</c>).
/// </summary>
/// <remarks>
/// Hands back the raw element rather than a number, because firmware tells "no such setting" and "not
/// a value it can parse" apart, and the policy answers each in its own words.
/// </remarks>
public static class LedIntensityArgument
{
    /// <summary>The kwarg's name, dotted as the telemetry block it is read back from.</summary>
    public const string Name = "chamber.led_intensity";

    /// <summary>
    /// The kwarg's value out of a <c>J</c> frame payload, or null when the payload carries none.
    /// </summary>
    public static JsonElement? TryFind(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("kwargs", out JsonElement kwargs) ||
                kwargs.ValueKind != JsonValueKind.Object ||
                !kwargs.TryGetProperty(Name, out JsonElement value))
            {
                return null;
            }

            // Cloned, because the document it belongs to is disposed on the way out.
            return value.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
