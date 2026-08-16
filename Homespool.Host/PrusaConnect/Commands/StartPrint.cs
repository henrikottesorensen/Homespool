using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Starts printing a file already on the printer's storage. Takes a path only - the file has to be
/// there already, which for a Connect-initiated print means a completed
/// <see cref="StartConnectDownload"/> first.
/// </summary>
/// <remarks>
/// <para>
/// Firmware declares this as <c>ArgPath</c> alone (command.cpp:163) and models the tool mapping as
/// <c>std::optional</c> (command.hpp:44-47), so a single-tool print sends just the path.
/// <see cref="Tool"/> is therefore nullable here, where it used to be <c>required</c>: a mapping
/// cannot be invented for a printer with one extruder, and requiring one made the ordinary case
/// unrepresentable.
/// </para>
/// <para>
/// The multi-tool shape is deliberately still not sent. Firmware parses <c>tool_mapping</c> as a
/// nested array of arrays (command.cpp:131-143), nothing here has ever produced one, and there is no
/// multi-tool printer to verify against - sending a wrong mapping to a real one is worse than
/// sending none.
/// </para>
/// </remarks>
public class StartPrint : ISendableCommand
{
    public required string Path { get; set; }

    /// <summary>Multi-tool only, and not yet put on the wire - see the remarks.</summary>
    public ToolMapping? Tool { get; set; }

    public string WireName => "START_PRINT";

    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["path"] = Path,
    };

    /// <inheritdoc />
    /// <remarks>Starting a print is the act queueing defers, so it is the same right - which is what lets <c>QueueAdvancer</c> send this as the person who queued the work.</remarks>
    public Capability RequiredCapability => Capability.Print;
}
