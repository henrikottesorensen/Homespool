using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// the receive side, <c>State</c> transitions, or the close handshake - only writing a frame.
/// Frames come from the printer's <see cref="PrinterConnectionActor"/> loop, and only from there.
/// </summary>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}
