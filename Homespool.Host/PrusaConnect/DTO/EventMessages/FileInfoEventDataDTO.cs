using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The <c>data</c> object on a <c>FILE_INFO</c> event - what a <c>SEND_FILE_INFO</c> comes back
/// with, and the only way to learn what is on a printer's own storage.
/// </summary>
/// <remarks>
/// <para>
/// One class covers both shapes, as <see cref="TransferEventDataDTO"/> does and for the same reason:
/// they are one object rendered by two branches of the same code (render.cpp:1006-1068 for the
/// directory variant, :466-498 for the file). A path that is a directory renders
/// <see cref="Children"/> and <see cref="FileCount"/>; a path that is a file renders the gcode's own
/// metadata instead. So every field is nullable and none may be assumed present.
/// </para>
/// <para>
/// <b>The names are the printer's, not ours.</b> Whatever the event was built from, the renderer
/// converts before it goes on the wire (render.cpp:1134-1135): <see cref="Path"/> is the 8.3 alias
/// and <see cref="DisplayName"/> the long name. In <see cref="Children"/> the same pair is
/// <c>name</c> and <c>display_name</c>. This is the one place a printer volunteers its own name for
/// a file - 205 of 206 observed child entries are aliased - and it is why this event answers a
/// question no transfer's own events can.
/// </para>
/// <para>
/// <b>Deliberately no <c>[JsonExtensionData]</c></b>, unlike <see cref="InfoEventDataDTO"/>. On the
/// file variant the unmodelled remainder is the uploaded gcode's own metadata headers - 407 keys and
/// a 71.6 KB thumbnail in one measured event - so an extension-data property would materialise an
/// attacker-influenced payload of unbounded size into a dictionary on every answer. That is the
/// hazard <c>TelemetryWriter.FormatPayload</c>'s allowlist already exists to keep off the
/// persistence side, and the same argument applies here.
/// </para>
/// <para>
/// Three behaviours of the directory listing worth knowing, all from render.cpp:1017-1036:
/// dot-files are skipped; a directory that is really an in-progress transfer is reported as a
/// <i>regular file</i> with <c>read_only: true</c>, because it is printable while still downloading;
/// and an incomplete <c>.bbf</c> is hidden entirely.
/// </para>
/// </remarks>
public class FileInfoEventDataDTO
{
    /// <summary>
    /// The printer's 8.3 alias for this path, e.g. <c>/usb/LAMPEN~1.BGC</c> - <b>not</b> the path
    /// that was asked for, even though asking is what produced it. <see cref="DisplayName"/> is the
    /// long name.
    /// </summary>
    /// <remarks>
    /// The conversion is on the reporting side only: the path we <i>send</i> is still the path that
    /// prints, since <c>MarlinPrinter::start_print</c> passes it on unconverted
    /// (marlin_printer.cpp:540). So this is a name to show, not one to store and print from.
    /// </remarks>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>The long name, as a person wrote it.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// <c>FOLDER</c> for a directory listing, <c>PRINT_FILE</c> for a file - firmware's own
    /// vocabulary, kept as the string it is.
    /// </summary>
    /// <remarks>
    /// Not an enum, deliberately: an unrecognised value would then throw while parsing an answer
    /// that is otherwise perfectly readable, which is the failure <c>ParseWireState</c>'s loud
    /// rejection has. A caller comparing against a known string degrades to
    /// "some other kind of entry" instead.
    /// </remarks>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Bytes, on the file variant.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>Last-modified, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("m_timestamp")]
    public long? ModifiedTimestamp { get; set; }

    [JsonPropertyName("read_only")]
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// The directory's entries, present only when the path asked about was a directory. Null and
    /// empty differ: null is a file, empty is a directory with nothing in it.
    /// </summary>
    [JsonPropertyName("children")]
    public IReadOnlyList<FileInfoChildDTO>? Children { get; set; }

    /// <summary>
    /// How many entries firmware put in <see cref="Children"/>. Rendered beside the array rather
    /// than derived from it, so a caller that trusts one over the other should trust the array.
    /// </summary>
    [JsonPropertyName("file_count")]
    public int? FileCount { get; set; }
}
