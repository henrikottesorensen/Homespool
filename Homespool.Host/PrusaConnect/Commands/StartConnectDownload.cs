using System.Collections.Generic;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Tells the printer to fetch a file from us. <c>START_CONNECT_DOWNLOAD</c> and
/// <c>START_INLINE_DOWNLOAD</c> map to the same firmware handler with the same argument set
/// (<c>ARGS_INLINE_DOWN</c>, command.cpp:89,186-193 at the pinned ref) - Connect deliberately sends
/// only the one command and lets the printer choose its mechanism, and Buddy always answers by
/// asking for byte ranges over the WebSocket it is already on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Hash"/> is ours to choose rather than something the printer verifies content against:
/// firmware only carries it back on the first range request of the transfer (render.cpp:100-119),
/// which makes it the correlation token between the command we sent and the request it provokes.
/// Firmware buffers it in 29 bytes including the terminator (command.hpp:73), hence
/// <see cref="MaxHashLength"/>.
/// </para>
/// <para>
/// <see cref="Path"/> must sit under <c>/usb</c> with no <c>/../</c> segment and carry a
/// transferrable extension, or the printer rejects the command outright (<c>path_allowed</c>,
/// planner.cpp:135-141). <see cref="OriginalSize"/> is <c>uint32</c> on the wire, so this cannot
/// describe a file above 4 GiB - a FatFS limit firmware's own comment acknowledges (command.hpp:64).
/// </para>
/// <para>
/// There is deliberately no port here, unlike <see cref="StartEncryptedDownload"/>: the inline
/// engine reuses the established connection rather than opening one, so it has nowhere to put a
/// port. An earlier version of this class carried one, copied from the encrypted variant.
/// </para>
/// </remarks>
public class StartConnectDownload : ISendableCommand
{
    /// <summary>Firmware's buffer, minus the null terminator (command.hpp:73).</summary>
    public const int MaxHashLength = 28;

    public required string Path { get; set; }

    public required string Hash { get; set; }

    public ulong TeamId { get; set; }

    public long OriginalSize { get; set; }

    public string WireName => "START_CONNECT_DOWNLOAD";

    /// <summary>
    /// All four kwargs firmware's <c>ARGS_INLINE_DOWN</c> requires. Its own parser tests reject the
    /// command as <c>BrokenCommand</c> when any one of them is missing
    /// (tests/unit/connect/command.cpp:141-151), so all four are always sent.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["path"] = Path,
        ["team_id"] = TeamId,
        ["hash"] = Hash,
        ["orig_size"] = OriginalSize,
    };
}
