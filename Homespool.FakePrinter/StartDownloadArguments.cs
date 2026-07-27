using System;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// The four keyword arguments a <c>START_CONNECT_DOWNLOAD</c> (or <c>START_INLINE_DOWNLOAD</c>)
/// carries - <c>ARGS_INLINE_DOWN</c>, Prusa-Firmware-Buddy <c>command.cpp:89</c> at the pinned ref.
/// Both spellings map to the same handler with the same arguments (<c>:186,193</c>).
/// </summary>
/// <param name="Path">Destination on the printer's storage.</param>
/// <param name="Hash">The server's transfer token, quoted back on the first range request.</param>
/// <param name="TeamId">Echoed on that request too.</param>
/// <param name="OriginalSize">Total bytes; <c>uint32</c> on the wire.</param>
public sealed record StartDownloadArguments(string Path, string Hash, ulong TeamId, long OriginalSize)
{
    /// <summary>
    /// Reads them out of a <c>J</c> frame's payload, or returns null if any is missing or the wrong
    /// shape - which firmware reports as <c>BrokenCommand{"Missing or broken parameters"}</c>
    /// (command.cpp:426). <b>All four are required</b>; firmware's own parser tests reject the command
    /// when any one is absent.
    /// </summary>
    public static StartDownloadArguments? TryParse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("kwargs", out JsonElement kwargs)
                || kwargs.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetString(kwargs, "path", out string? path)
                || !TryGetString(kwargs, "hash", out string? hash)
                || !kwargs.TryGetProperty("team_id", out JsonElement teamId)
                || !teamId.TryGetUInt64(out ulong team)
                || !kwargs.TryGetProperty("orig_size", out JsonElement origSize)
                || !origSize.TryGetInt64(out long size))
            {
                return null;
            }

            return new StartDownloadArguments(path, hash, team, size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;

        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;

        return true;
    }
}
