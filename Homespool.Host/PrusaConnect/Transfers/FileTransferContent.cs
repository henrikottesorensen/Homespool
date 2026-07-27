using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// An offered file on local disk, read positionally through
/// <see cref="RandomAccess.ReadAsync(SafeFileHandle, Memory{byte}, long, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RandomAccess"/> rather than a <see cref="FileStream"/> because there is no seek pointer
/// to share: each read states its own offset, so nothing has to serialize access to a cursor, and
/// firmware's <c>RangeJump</c> - which grabs a file's head and tail first to scan the embedded
/// preview - is just a read somewhere else rather than a seek that could race.
/// </para>
/// <para>
/// And rather than a memory-mapped file, which looks like a better fit for this access pattern and is
/// not: a mapped read happens in a page fault at the moment the bytes are touched, which here would
/// be inside <c>WebSocket.SendAsync</c> while the write lock is held. That is an unbounded,
/// uncancellable stall in the worst possible place - and on a network mount an I/O error arrives as
/// <c>SIGBUS</c> rather than as an exception. An ordinary async read is bounded by the caller's
/// timeout and fails as an exception.
/// </para>
/// <para>
/// Opened <see cref="FileShare.Read"/>: the file may be replaced or deleted while a printer is
/// pulling it, and the handle keeps the bytes alive for this transfer either way.
/// </para>
/// </remarks>
public sealed class FileTransferContent : ITransferContent
{
    private readonly SafeFileHandle _handle;

    public FileTransferContent(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken) =>
        RandomAccess.ReadAsync(_handle, destination, offset, cancellationToken);

    public void Dispose() => _handle.Dispose();
}
