using System.Collections.Generic;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Asks the printer about a path on its own storage. <b>On a directory it enumerates it</b>, which
/// is the only listing mechanism the 26-command vocabulary has - there is no "list files" command
/// (<c>planner.cpp:751-759</c>, and the <c>DirRenderer</c> variant at render.cpp:1006-1068).
/// </summary>
/// <remarks>
/// <para>
/// The first command whose answer is a payload rather than a verdict, hence
/// <see cref="ISendableCommand{TAnswer}"/> - firmware replies with a <c>FILE_INFO</c> carrying the
/// same <c>command_id</c>, and the listing is inside its <c>data</c>. Send it with
/// <see cref="PrinterCommandService.AskAsync"/> rather than
/// <see cref="PrinterCommandService.SendCommandAsync"/>, which reports the verdict and drops the
/// answer.
/// </para>
/// <para>
/// <b>On a file the answer is enormous</b> - a measured 90 385 bytes, of which 71 588 is the preview
/// thumbnail, across 407 keys of the gcode's own metadata. That is the largest message a printer
/// sends, and <see cref="FileInfoEventDataDTO"/> models firmware's own fixed fields rather than
/// trying to hold the rest.
/// </para>
/// <para>
/// <see cref="Path"/> is a path on the printer, not one of ours - <c>/usb</c> for the drive's root.
/// A path firmware will not answer for comes back as an ordinary <c>Rejected</c> outcome rather than
/// as an exception.
/// </para>
/// </remarks>
public class SendFileInfo : ISendableCommand<FileInfoEventDataDTO>
{
    public required string Path { get; set; }

    public string WireName => "SEND_FILE_INFO";

    /// <summary>
    /// The one kwarg firmware reads (<c>planner.cpp:751-759</c>). It is also the only command
    /// argument that decides the <i>shape</i> of the answer rather than just its content: a
    /// directory renders children, a file renders metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["path"] = Path,
    };

    /// <inheritdoc />
    /// <remarks>It asks a question and changes nothing. An endpoint exposing it may still gate itself higher.</remarks>
    public Capability RequiredCapability => Capability.ViewPrinter;
}
