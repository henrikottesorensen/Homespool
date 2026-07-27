using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// The bytes behind one offered transfer, read positionally. Deliberately not a <c>Stream</c>: every
/// read carries its own offset, so there is no shared seek pointer for the connection and the actor
/// to coordinate, and firmware's <c>RangeJump</c> - which fetches a file's head and tail first to
/// scan the preview (transfer.hpp:54-77) - is an ordinary read at a different offset rather than a
/// seek that something else might have moved.
/// </summary>
public interface ITransferContent : IDisposable
{
    /// <summary>Total bytes, which is what we declare to the printer as <c>orig_size</c>.</summary>
    long Length { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="offset"/>, returning the number of
    /// bytes actually read - which may be short, exactly as an ordinary read may be.
    /// </summary>
    /// <remarks>
    /// Genuinely asynchronous, and that is the point rather than a style preference: it is the one
    /// property that lets a slow disk be bounded by a timeout at the call site. A memory-mapped file
    /// would move the same I/O into a page fault, which has no await point, no timeout and no
    /// cancellation - and would take it inside the socket write lock, where it could block teardown.
    /// </remarks>
    ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken);
}
