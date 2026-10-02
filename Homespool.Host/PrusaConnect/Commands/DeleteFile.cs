using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Deletes one file from the printer's storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Success is <c>FILE_CHANGED</c>, not <c>FINISHED</c></b> (planner.cpp:882-900): firmware reports
/// the deletion as a change to the path, under this command's id, and never sends a verdict of its
/// own. Test for the absence of a refusal, as for <see cref="StartPrint"/>.
/// </para>
/// <para>
/// <b>Firmware refuses a file in use</b> - <c>File is busy</c> while it prints, <c>File is being
/// transferred</c> while it arrives (marlin_printer.cpp:549-560) - so a delete cannot take the bytes
/// out from under a running print. <c>File not found</c> means it is already gone.
/// </para>
/// <para>
/// <b>Sent only for a copy Homespool put on the drive itself</b>: an older version of one of a
/// user's files, under the name it chose for it. Deleting anything else on a printer is a different
/// act with a different audience, and would want a capability of its own rather than this one.
/// </para>
/// </remarks>
public class DeleteFile : ISendableCommand
{
    public required string Path { get; set; }

    public string WireName => "DELETE_FILE";

    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["path"] = Path,
    };

    /// <inheritdoc />
    /// <remarks>
    /// The same right as the transfer it makes room for: removing an outdated copy of somebody's own
    /// file is part of sending them the current one.
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
