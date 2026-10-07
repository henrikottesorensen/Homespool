using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Host.PrintFiles;
using Homespool.Host.PrintFiles.GCode;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// The slicer's preview of a print, read from the file it was sent from and remembered per print.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the file here rather than asked of the printer.</b> Every file sliced for these
/// printers carries a PNG preview, and the stored copy answers without a wire question. What that
/// misses is a print started at the printer's own panel: it has no <see cref="PrintJob"/>, so there
/// is nothing to look up and no preview.
/// </para>
/// <para>
/// <b>The file is found the way a reprint finds it</b>: by name among the files of whoever queued the
/// print - <see cref="PrintJob.FileName"/> is a record of what ran, not a pointer at it - and only
/// while its digest still matches the one the print opened with. A file overwritten since would
/// otherwise show a picture of something that is not printing. Where either digest is unknown there
/// is nothing to compare, and the file is taken as it is.
/// </para>
/// <para>
/// <b>Remembered per print, in memory and nowhere else.</b> The status card asks on every poll, so the
/// file is read once per print rather than every two seconds per viewer; and a preview belongs to one
/// print, so a remembered one never goes stale - even if the file is replaced later, the picture is of
/// what was printed. A preview is not written to the database. The last <see cref="Capacity"/> prints
/// are held, a few printers' worth of about 53 KB each.
/// </para>
/// <para>
/// <b>One read per print, whoever is asking.</b> What is remembered is the read itself, so viewers and
/// polls arriving together wait on the same one rather than each starting their own. It runs on none of
/// their cancellations, for the same reason: another viewer may be waiting on it. A read that fails is
/// forgotten, so the next poll tries again.
/// </para>
/// </remarks>
public sealed class PrintThumbnails
{
    /// <summary>How many prints' answers are held at once, the oldest dropped first.</summary>
    public const int Capacity = 32;

    private readonly UserFileStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrintThumbnails> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Task<byte[]?>> _reads = [];
    private readonly Queue<Guid> _order = new();

    public PrintThumbnails(UserFileStore store, IServiceScopeFactory scopeFactory, ILogger<PrintThumbnails> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The PNG preview of <paramref name="job"/>'s file, or null when there is none to show.</summary>
    /// <remarks>
    /// Authorises nothing: it takes a print the caller already holds, and reaching one has been through
    /// <see cref="PrintHistoryService"/>'s own check.
    /// </remarks>
    public async Task<byte[]?> ForAsync(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        Task<byte[]?>? read;

        lock (_gate)
        {
            if (!_reads.TryGetValue(job.PrintUuid, out read))
            {
                // Off this caller's stack before anything is read, so the lock is never held across it.
                read = Task.Run(() => ReadAsync(job, CancellationToken.None), CancellationToken.None);

                _reads[job.PrintUuid] = read;
                _order.Enqueue(job.PrintUuid);

                while (_order.Count > Capacity)
                {
                    _reads.Remove(_order.Dequeue());
                }

                _ = read.ContinueWith(failed => Forget(job.PrintUuid, failed), CancellationToken.None,
                                      TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        return await read.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Drops a failed read, so the next caller starts another. Only if it is still the one held: a
    /// retry may already have taken its place.
    /// </summary>
    private void Forget(Guid printUuid, Task<byte[]?> failed)
    {
        _logger.LogWarning(failed.Exception, "[{PrintUuid}] could not read the print's preview", printUuid);

        lock (_gate)
        {
            if (_reads.TryGetValue(printUuid, out Task<byte[]?>? held) && held == failed)
            {
                _reads.Remove(printUuid);
            }
        }
    }

    private async Task<byte[]?> ReadAsync(PrintJob job, CancellationToken cancellationToken)
    {
        StoredFile? file = _store.Find(job.QueuedByUserId, job.FileName);

        if (file is null)
        {
            return null;
        }

        if (job.Digest is not null)
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();

            PrintFile? row = await catalog.RowForAsync(job.QueuedByUserId, file, cancellationToken);

            if (row?.Digest is not null && !string.Equals(row.Digest, job.Digest, StringComparison.Ordinal))
            {
                _logger.LogDebug("[{PrinterId}] no preview for print {PrintUuid}: its file has changed since it opened",
                                 job.PrinterId, job.PrintUuid);

                return null;
            }
        }

        return GCodeThumbnailReader.ReadFile(file.Path);
    }
}
