using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Host.PrusaConnect.DTO.EventMessages;

namespace Homespool.Host.DTO;

/// <summary>
/// What is at one path on a printer's own storage - a directory and its entries, or a single file.
/// </summary>
/// <remarks>
/// <para>
/// The answer to a <c>SEND_FILE_INFO</c>, translated out of firmware's vocabulary. See
/// <see cref="PrinterStorageEntryDTO"/> for why translating rather than passing the wire shape
/// through.
/// </para>
/// <para>
/// <b><see cref="Path"/> is the printer's answer, not the request.</b> Firmware converts before
/// rendering (render.cpp:1134-1135), so asking about a long name comes back describing the 8.3
/// alias. A client that navigates by long name therefore gets a path it did not ask for, and that is
/// the printer being authoritative about its own filesystem rather than a bug.
/// </para>
/// </remarks>
public class PrinterStorageReadDTO
{
    /// <summary>The path firmware answered about, in its own 8.3 form.</summary>
    public string? Path { get; set; }

    /// <summary>The long name of the thing at that path.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// <c>folder</c> for a directory, <c>printFile</c> for something printable, <c>file</c> for
    /// anything else firmware will name - a captured listing had
    /// <c>prusa_printer_settings.ini</c> come back as plain <c>FILE</c>.
    /// </summary>
    /// <remarks>
    /// Converted from firmware's <c>SCREAMING_SNAKE</c> mechanically rather than through a lookup,
    /// so a value nobody has seen yet arrives as a sensible camelCase string instead of being
    /// dropped or throwing. The set is firmware's to grow.
    /// </remarks>
    public string? Kind { get; set; }

    public bool? ReadOnly { get; set; }

    /// <summary>
    /// The directory's entries. <b>Null and empty differ</b>: null means the path was a file, empty
    /// means a directory with nothing in it.
    /// </summary>
    public IReadOnlyList<PrinterStorageEntryDTO>? Entries { get; set; }

    public static PrinterStorageReadDTO FromEvent(FileInfoEventDataDTO data)
    {
        return new()
        {
            Path = data.Path,
            Name = data.DisplayName,
            Kind = ToKind(data.Type),
            ReadOnly = data.ReadOnly,
            Entries = data.Children?.Select(PrinterStorageEntryDTO.FromChild).ToList(),
        };
    }

    /// <summary>Firmware's Unix seconds as an instant, or null when it sent none.</summary>
    internal static DateTimeOffset? ToInstant(long? unixSeconds)
    {
        return unixSeconds is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary><c>PRINT_FILE</c> becomes <c>printFile</c>, <c>FOLDER</c> becomes <c>folder</c>.</summary>
    internal static string? ToKind(string? wireType)
    {
        if (string.IsNullOrEmpty(wireType))
        {
            return null;
        }

        string[] words = wireType.Split('_', StringSplitOptions.RemoveEmptyEntries);

        return string.Concat(words.Select((word, index) =>
                                              index == 0 ?
                                                  word.ToLowerInvariant() :
                                                  char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }
}
