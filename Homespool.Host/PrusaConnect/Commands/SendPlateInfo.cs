using System.Collections.Generic;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// <c>SEND_FILE_INFO</c> for a print file, asked for its plate rather than for the file - the same
/// wire command as <see cref="SendFileInfo"/>, read into a different shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second class rather than a second reading of the first</b>, because the answer's shape is
/// declared by the command's type (<see cref="ISendableCommand{TAnswer}"/>): one class per question is
/// what lets <c>AskAsync</c> stay a single deserialisation with no switching.
/// </para>
/// <para>
/// <b>Asked of the printer rather than read from a file held here</b>, so it describes whatever the
/// printer is running - a print started at the panel as much as one sent from here - and costs one
/// large answer, about 90 KB with the thumbnail, once per print.
/// </para>
/// </remarks>
public class SendPlateInfo : ISendableCommand<PlateInfoEventDataDTO>
{
    /// <summary>The file's path on the printer, as <c>JOB_INFO</c> reports it for the running job.</summary>
    public required string Path { get; set; }

    public string WireName => "SEND_FILE_INFO";

    /// <summary>The one kwarg, as for <see cref="SendFileInfo"/>.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["path"] = Path,
    };

    /// <inheritdoc />
    /// <remarks>It asks a question and changes nothing.</remarks>
    public Capability RequiredCapability => Capability.ViewPrinter;
}
