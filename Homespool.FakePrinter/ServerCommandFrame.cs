using System;
using System.Text;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// A parsed server-to-printer frame: 9-byte header (type char + 8 hex digits of command id), then
/// the payload. Parsing mirrors <c>receive_command</c>
/// (Prusa-Firmware-Buddy <c>connect.cpp:440-521</c> at the pinned ref), including which reason
/// string each malformation earns and which command id (0 or the parsed one) it is reported under.
/// </summary>
/// <param name="Kind">The command-type character.</param>
/// <param name="CommandId">The 8-hex-digit command id from the header.</param>
/// <param name="Payload">The bytes after the header - JSON for <c>J</c>, raw for <c>G</c>/<c>T</c>.</param>
public sealed record ServerCommandFrame(ServerCommandKind Kind, uint CommandId, ReadOnlyMemory<byte> Payload)
{
    private const int HeaderLength = 9;

    /// <summary>
    /// Parses one whole WebSocket message. On a malformed header the result's
    /// <see cref="FrameParseResult.Broken"/> carries the exact rejection the firmware would plan
    /// (<c>BrokenCommand</c> reason, and command id 0 when the header itself was unreadable).
    /// </summary>
    public static FrameParseResult Parse(ReadOnlyMemory<byte> message)
    {
        if (message.Length < HeaderLength)
        {
            // connect.cpp:450-452 - too short even for the header; reported under command id 0.
            return new FrameParseResult(null, new BrokenFrame(0, "Message too short to contain header"));
        }

        ReadOnlySpan<byte> span = message.Span;
        Span<char> hex = stackalloc char[8];

        for (int i = 0; i < 8; i++)
        {
            hex[i] = (char)span[1 + i];
        }

        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint commandId))
        {
            // connect.cpp:455-460 - from_chars_light failed; also reported under command id 0.
            return new FrameParseResult(null, new BrokenFrame(0, "Could not parse command ID"));
        }

        char kind = (char)span[0];

        if (kind is not ('J' or 'G' or 'F' or 'D' or 'T'))
        {
            // connect.cpp:518-521 - the id parsed, so the rejection carries it.
            return new FrameParseResult(null, new BrokenFrame(commandId, "Unrecognized type of message"));
        }

        return new FrameParseResult(new ServerCommandFrame((ServerCommandKind)kind, commandId, message[HeaderLength..]), null);
    }

    /// <summary>
    /// For a <see cref="ServerCommandKind.Json"/> frame: the <c>"command"</c> property's value, or
    /// null when the payload is not valid JSON or carries no such property (the firmware's
    /// <c>parse_json_command</c> distinguishes the two - see
    /// <see cref="FirmwareFaithfulPolicy"/> for how each is answered).
    /// </summary>
    public string? TryGetJsonCommandName()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Payload);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("command", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the payload parses as JSON at all - decides "Error parsing JSON" vs "Unknown command".</summary>
    public bool PayloadIsValidJson()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Payload);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The payload decoded as UTF-8, for logs and assertions.</summary>
    public string PayloadText()
    {
        return Encoding.UTF8.GetString(Payload.Span);
    }
}
