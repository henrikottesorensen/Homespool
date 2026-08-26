using System.Collections.Generic;

namespace Homespool.Host.Pages;

/// <summary>
/// One file named in a drop, and whether the reader already has one by that name.
/// </summary>
/// <param name="Name">The file name as the browser reported it.</param>
/// <remarks>
/// <b>What a printer will take is decided here, not in the browser.</b> tile-drop.js used to carry
/// its own list of extensions and silently drop anything else - so an STL landed on a tile and
/// nothing whatsoever happened, which reads as a broken feature rather than a refused file. The list
/// it carried had also drifted from <see cref="PrintFiles.UserFileStore.AllowedExtensions"/>, being
/// wrong in both directions at once. There is now one list, on the server, and the refusal is a
/// sentence.
/// </remarks>
/// <param name="Conflicts">
/// Whether a file of this name is already in the reader's own tree. Answered before a byte moves, so
/// the name clash is settled while the drop can still be abandoned for free.
/// </param>
/// <param name="Allowed">
/// Whether a printer would take a file of this name at all. False for an STL, a project file, a
/// photograph - anything that has not been sliced.
/// </param>
public sealed record TileDropFile(string Name, bool Conflicts, bool Allowed);

/// <summary>
/// What the dialog raised by a drop onto a tile needs to know.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two questions in a fixed order, and the order was asked for.</b> Name clashes are settled
/// first, then what to do with the files - because "keep or replace" is a question about the bytes
/// and is worth answering before deciding to print with them, not after.
/// </para>
/// <para>
/// <b>The clash check runs on names alone, before any upload.</b> That is what makes the first
/// question free to answer: nothing has been written, so "keep" costs nothing and "replace" has not
/// yet destroyed anything. The Files page reaches the same question from the other side - it uploads,
/// catches <c>PrintFileNameConflictException</c> and asks afterwards - which is right there, where a
/// single upload is the whole intent, and wrong here, where the upload is a step on the way to
/// printing.
/// </para>
/// </remarks>
/// <param name="PrinterUuid">Which printer was dropped onto - the handle pages already carry.</param>
/// <param name="PrinterName">
/// Its name, which the dialog states. A drop onto the wrong tile is otherwise only obvious once
/// something has started printing on the wrong machine.
/// </param>
/// <param name="Files">
/// The dropped files, each flagged for a name clash. <b>A drag contributes one</b> - tile-drop.js
/// takes the first print file it finds - but this stays a list because the handler behind it is an
/// endpoint, and one that fell over on a second file would be a rule enforced only by the script
/// that happens to call it.
/// </param>
/// <param name="CanPrint">Whether to offer queueing at all.</param>
/// <param name="CanReady">Whether to offer readying and printing now.</param>
/// <param name="CameraFrameUrl">
/// Where to poll a still of this printer, or null if it has no camera. <b>Shown on the bed-clear
/// step only</b>, which is the one question on this dialog that cannot be answered from the desk -
/// the printer page puts the same view in its own Set ready modal for the same reason.
/// </param>
/// <param name="AcceptedExtensions">
/// What a printer will take, joined for reading, from
/// <see cref="PrintFiles.UserFileStore.AllowedExtensions"/> rather than written out again.
/// </param>
/// <param name="MaxUploadSize">
/// The upload cap, already formatted for a person. Only shown when nothing printable was dropped,
/// where the reader needs to know what this page does take.
/// </param>
/// <param name="CanReplace">
/// Whether replacing is even available to this reader - overwriting is
/// <c>ManipulateOwnFiles</c>, a capability apart from uploading. Without it the clash question has
/// one answer, and the dialog says so rather than offering a button that would be refused.
/// </param>
public sealed record TileDropPrompt(
    System.Guid PrinterUuid,
    string PrinterName,
    IReadOnlyList<TileDropFile> Files,
    bool CanPrint,
    bool CanReady,
    bool CanReplace,
    string? CameraFrameUrl,
    string AcceptedExtensions,
    string MaxUploadSize);
