using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrintFiles.GCode;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Brings the file index back into agreement with the disk at startup, and keeps what it says about
/// each file current while the service runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filesystem is the truth, so this only ever teaches the table</b> - it adds rows for files it
/// finds, corrects sizes and timestamps that moved, and removes rows for files that are gone. It never
/// creates, renames or deletes a file to match a row. That direction is what makes hand-managing the
/// data directory a supported thing to do rather than a way to corrupt state.
/// </para>
/// <para>
/// <b>Reading comes after each pass, and nothing waits for it.</b> Once the index agrees with the
/// disk, <see cref="BackfillAsync"/> reads every file whose row has no <see cref="PrintFile.Digest"/>
/// or has not had its slicer metadata read - one that arrived outside the app, one indexed on the way
/// to a print, one whose bytes moved - one at a time, while the service is already serving. Uploads do
/// both on the pass they already make, so there is usually nothing left to read.
/// </para>
/// <para>
/// <b>The full reconcile once; a narrower pass on a timer.</b> Every path that changes a file goes
/// through <see cref="PrintFileCatalog"/> and keeps both halves in step, and the read paths heal a
/// missing row on the spot. So adding and removing rows is for what happened while the process was
/// <i>not</i> running - a restore, a hand-copied file, an interrupted write. What the catalog cannot
/// see while it runs is a file edited in place by hand, whose row would go on carrying a digest the
/// reprint check believes and metadata the compatibility check believes, and a row indexed on the way
/// to a print, which would wait for a restart to be read. <see cref="RecheckAsync"/> covers those every <see cref="RecheckInterval"/>, and says why it
/// does no more.
/// </para>
/// </remarks>
public sealed class PrintFileReconciler : BackgroundService
{
    /// <summary>How often the running service rechecks the files it has indexed.</summary>
    /// <remarks>
    /// Bounds how long a hand edit's stale digest can be believed. A pass that finds nothing costs one
    /// directory read per row and one query, so the interval is about how stale is tolerable, not
    /// about cost.
    /// </remarks>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserFileStore _store;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PrintFileReconciler> _logger;

    public PrintFileReconciler(IServiceScopeFactory scopeFactory,
                               UserFileStore store,
                               IOptionsMonitor<PrintFileStorageOptions> options,
                               IHostEnvironmentAccessor environment,
                               TimeProvider timeProvider,
                               ILogger<PrintFileReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _scopeFactory = scopeFactory;
        _store = store;
        _root = Path.IsPathRooted(options.CurrentValue.Directory) ?
            options.CurrentValue.Directory :
            Path.Combine(environment.ContentRootPath, options.CurrentValue.Directory);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(RecheckInterval, _timeProvider);

        try
        {
            // Each backfill after its pass, never alongside it: what it reads is only worth writing to
            // a row that describes the file on disk, and the pass is what makes the two agree.
            await RunPassAsync(ReconcileAsync, stoppingToken);
            await RunPassAsync(BackfillAsync, stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunPassAsync(RecheckAsync, stoppingToken);
                await RunPassAsync(BackfillAsync, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-pass. Nothing is half-applied that matters - the next start runs it
            // again, and the read paths heal whatever it did not reach.
        }
    }

    /// <summary>Runs one pass, logging a failure instead of ending the loop.</summary>
    /// <remarks>
    /// A stale index is a far smaller problem than a service that stops keeping it, and every reader
    /// can cope with one: a missing row is created on demand, and an extra row is only reachable
    /// through a file that is not there. So a failed pass is logged and the next one tries again - and
    /// the passes after a failed reconcile still run, because each only writes what it has just
    /// checked against the disk.
    /// </remarks>
    private async Task RunPassAsync(Func<CancellationToken, Task> pass, CancellationToken stoppingToken)
    {
        try
        {
            await pass(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Print-file index pass {Pass} failed; the index may be out of date until the next one.",
                             pass.Method.Name);
        }
    }

    /// <summary>Walks the store and applies the differences. Public so a test can run it directly.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HashSet<long> users = [.. await dbContext.Users.Select(user => user.Id).ToListAsync(cancellationToken)];
        Dictionary<long, List<StoredFile>> onDisk = ReadDisk(users);

        List<PrintFile> rows = await dbContext.PrintFiles.ToListAsync(cancellationToken);
        int added = 0, corrected = 0, removed = 0;

        foreach ((long userId, List<StoredFile> files) in onDisk)
        {
            Dictionary<string, PrintFile> byName = rows.Where(row => row.UserId == userId)
                                                       .ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);

            foreach (StoredFile file in files)
            {
                if (!byName.TryGetValue(file.FileName, out PrintFile? row))
                {
                    dbContext.PrintFiles.Add(new PrintFile
                    {
                        UserId = userId,
                        Name = file.FileName,
                        Size = file.Length,
                        Digest = null,
                        UploadedAt = file.UploadedAt,

                        // Indexed without being read, like the digest left null: the backfill after
                        // this pass reads both. Unread says exactly that; the default would say
                        // nothing.
                        MetadataState = PrintFileMetadataState.Unread,
                    });

                    added++;

                    continue;
                }

                if (HasMoved(row, file))
                {
                    // The bytes changed underneath us. The digest and the metadata are now statements
                    // about content that is gone, so they are cleared rather than left to be believed -
                    // and the backfill reads what is there now, so a file that only looked changed, its
                    // timestamp coarsened by a copy through another filesystem, gets the same answers
                    // back.
                    row.Size = file.Length;
                    row.UploadedAt = file.UploadedAt;
                    row.Digest = null;
                    PrintFileMetadata.Forget(row);

                    corrected++;
                }
            }
        }

        foreach (PrintFile row in rows)
        {
            if (onDisk.TryGetValue(row.UserId, out List<StoredFile>? files) &&
                files.Exists(file => string.Equals(file.FileName, row.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The file is gone, so any queue entry pointing at it is a job that can never print. The
            // foreign key refuses this on the user-facing delete path deliberately - so that a person
            // is asked rather than surprised - but there is nobody to ask here and nothing to preserve:
            // the bytes already left without going through us.
            List<QueuedPrint> orphaned = await dbContext.QueuedPrints
                                                        .Where(job => job.PrintFileId == row.Id)
                                                        .ToListAsync(cancellationToken);

            if (orphaned.Count > 0)
            {
                _logger.LogWarning(
                    "{FileName} (user {UserId}) is gone from disk but {Count} queued print(s) referenced it; " +
                    "cancelling them - the file was removed outside Homespool.",
                    row.Name, row.UserId, orphaned.Count);

                dbContext.QueuedPrints.RemoveRange(orphaned);
            }

            dbContext.PrintFiles.Remove(row);
            removed++;
        }

        if (added + corrected + removed == 0)
        {
            _logger.LogDebug("Print-file index already matched the disk.");

            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Reconciled the print-file index: {Added} added, {Corrected} corrected, {Removed} removed.",
            added, corrected, removed);
    }

    /// <summary>
    /// Corrects every row whose file has changed on disk since the row was written, while the service
    /// runs. Public so a test can run it directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The reconcile's middle third, and only that.</b> Adding and removing rows are left to the
    /// startup pass, because each races an operation that changes the disk before it changes the
    /// table. A rename moves the file and then renames its row, so a removal in between would cancel
    /// the queued prints that keeping the row is there to protect. An upload publishes the file and
    /// then inserts its row, so an insert in between would collide on the unique name. Meanwhile a file
    /// with no row is listed anyway and indexed when printed, and a row with no file is only reachable
    /// through a file that is not there.
    /// </para>
    /// <para>
    /// <b>Each write is conditional on the row still saying what it said when it was read</b>, so an
    /// upload overwriting the file - which writes its own size, timestamp, digest and metadata - either
    /// lands after this and wins, or lands first and leaves this nothing to change. A file that cannot be
    /// found is skipped, not removed: that is a rename or a delete in progress.
    /// </para>
    /// </remarks>
    public async Task RecheckAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        List<PrintFile> rows = await dbContext.PrintFiles.AsNoTracking().ToListAsync(cancellationToken);
        int corrected = 0;

        PrintFile forgotten = new() { Name = string.Empty };
        PrintFileMetadata.Forget(forgotten);

        foreach (PrintFile row in rows)
        {
            StoredFile? file = _store.Find(row.UserId, row.Name);

            if (file is null || !HasMoved(row, file))
            {
                continue;
            }

            corrected += await dbContext.PrintFiles
                                        .Where(candidate => candidate.Id == row.Id &&
                                                            candidate.Size == row.Size &&
                                                            candidate.UploadedAt == row.UploadedAt)
                                        .ExecuteUpdateAsync(set =>
                                                            {
                                                                set.SetProperty(candidate => candidate.Size, file.Length)
                                                                   .SetProperty(candidate => candidate.UploadedAt, file.UploadedAt)
                                                                   .SetProperty(candidate => candidate.Digest, (string?)null);

                                                                PrintFileMetadata.Set(set, forgotten);
                                                            },
                                                            cancellationToken);
        }

        if (corrected > 0)
        {
            _logger.LogInformation("Rechecked the print-file index: {Corrected} corrected.", corrected);
        }
    }

    /// <summary>
    /// Whether the file on disk is no longer the one <paramref name="row"/> describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>At the column's precision.</b> It holds whole milliseconds and the filesystem ticks, so
    /// compared exactly every file reads as changed on every pass and loses its digest and metadata.
    /// </para>
    /// <para>
    /// <b>Size and timestamp, never size alone</b>: an edit that keeps the length - a temperature from
    /// 215 to 220 - is exactly the change the reprint check exists to notice. A timestamp that moves
    /// without the bytes - a copy through a filesystem with coarser times - costs a re-hash, which
    /// gives the same digest back.
    /// </para>
    /// </remarks>
    private static bool HasMoved(PrintFile row, StoredFile file)
    {
        return row.Size != file.Length ||
               row.UploadedAt.ToUnixTimeMilliseconds() != file.UploadedAt.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Reads every indexed file whose row has no digest or has not had its slicer metadata read, one at
    /// a time. Public so a test can run it directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One open per file, and only the reads it needs.</b> A missing digest costs the whole file; a
    /// missing description costs a header and a tail, which is why a row that only lacks the second
    /// is cheap to finish. <see cref="PrintFileMetadataState.Undefined"/> - rows written before the
    /// state existed - counts as unread, as the API already reports it.
    /// </para>
    /// <para>
    /// <b>What is written describes only the bytes that were read.</b> The size and timestamp are read
    /// from the handle, after its last read, and each write is conditional on the row still carrying
    /// exactly those, and still lacking what is being written: no digest, or the metadata state that
    /// was read with the row. An upload replacing the file mid-read moves a new file into place while
    /// this goes on reading the old one, then writes its own digest and metadata - so the row either
    /// has them or no longer matches, and these writes update nothing. A file edited in place while it
    /// is read moves its own timestamp, with the same result. Either way the next pass tries again.
    /// </para>
    /// <para>
    /// A file that cannot be read is skipped rather than fatal: the rest of the store still deserves
    /// reading, and the row keeps what it already had.
    /// </para>
    /// </remarks>
    public async Task BackfillAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        List<PrintFile> wanting = await dbContext.PrintFiles
                                                 .AsNoTracking()
                                                 .Where(row => row.Digest == null ||
                                                               row.MetadataState == PrintFileMetadataState.Unread ||
                                                               row.MetadataState == PrintFileMetadataState.Undefined)
                                                 .ToListAsync(cancellationToken);

        long started = Stopwatch.GetTimestamp();
        int hashed = 0, described = 0;
        long bytes = 0;

        foreach (PrintFile row in wanting)
        {
            StoredFile? file = _store.Find(row.UserId, row.Name);

            if (file is null)
            {
                // Gone since the pass before this; the next start removes the row.
                continue;
            }

            bool describe = row.MetadataState is PrintFileMetadataState.Unread or PrintFileMetadataState.Undefined;
            FileReading? reading = await ReadAsync(row.UserId, file, hash: row.Digest is null, describe, cancellationToken);

            if (reading is null)
            {
                continue;
            }

            int written = 0;

            if (reading.Digest is not null)
            {
                int filled = await dbContext.PrintFiles
                                            .Where(candidate => candidate.Id == row.Id &&
                                                                candidate.Digest == null &&
                                                                candidate.Size == reading.Length &&
                                                                candidate.UploadedAt == reading.WrittenAt)
                                            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.Digest, reading.Digest),
                                                                cancellationToken);

                hashed += filled;
                bytes += filled * reading.Length;
                written += filled;
            }

            if (reading.Description is not null)
            {
                int filled = await dbContext.PrintFiles
                                            .Where(candidate => candidate.Id == row.Id &&
                                                                candidate.MetadataState == row.MetadataState &&
                                                                candidate.Size == reading.Length &&
                                                                candidate.UploadedAt == reading.WrittenAt)
                                            .ExecuteUpdateAsync(set => PrintFileMetadata.Set(set, reading.Description),
                                                                cancellationToken);

                described += filled;
                written += filled;
            }

            if (written == 0)
            {
                _logger.LogDebug("{FileName} (user {UserId}) no longer matches its row; it waits for the next pass.",
                                 row.Name, row.UserId);

                continue;
            }

            _logger.LogDebug("Read {FileName} (user {UserId}) into its row.", row.Name, row.UserId);
        }

        if (hashed + described > 0)
        {
            _logger.LogInformation(
                "Backfilled the print-file index: {Hashed} digests ({Bytes} bytes) and {Described} descriptions, in {Elapsed}.",
                hashed, bytes, described, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// What one file's bytes say - its digest, its slicer metadata as row columns, or both - with the
    /// size and timestamp of the bytes they describe, or null when the file could not be read.
    /// </summary>
    private async Task<FileReading?> ReadAsync(long userId,
                                               StoredFile file,
                                               bool hash,
                                               bool describe,
                                               CancellationToken cancellationToken)
    {
        try
        {
            // Shared for writing and deleting, so that nothing waits on this read: an upload replacing
            // the file, or somebody removing it, goes ahead, and the conditional writes sort it out.
            await using FileStream stream = new(file.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });

            string? digest = hash ? await PrintFileDigest.ComputeAsync(stream, copyTo: null, cancellationToken) : null;
            PrintFile? description = null;

            if (describe)
            {
                description = new PrintFile { Name = file.FileName };
                PrintFileMetadata.Apply(description, GCodeMetadataReader.Read(stream));
            }

            // After the last read, and from the handle rather than the path: the path may already name
            // a replacement, and these have to describe what was read.
            return new FileReading(digest,
                                   description,
                                   RandomAccess.GetLength(stream.SafeFileHandle),
                                   new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read {FileName} (user {UserId}) to fill in its row; skipping it.",
                               file.FileName, userId);

            return null;
        }
    }

    /// <summary>
    /// What is actually on disk, per user - skipping directories that are not a user's.
    /// </summary>
    /// <remarks>
    /// <b>A directory whose user no longer exists is left entirely alone</b>, neither indexed nor
    /// deleted. Indexing it would violate the row's foreign key, and deleting the files would be this
    /// class writing to the disk, which it does not do. So the bytes of a removed account sit there
    /// until somebody clears them out by hand - visible, which is the right failure for something
    /// nothing in the app currently handles.
    /// </remarks>
    private Dictionary<long, List<StoredFile>> ReadDisk(HashSet<long> users)
    {
        Dictionary<long, List<StoredFile>> found = [];

        if (!Directory.Exists(_root))
        {
            return found;
        }

        foreach (string directory in Directory.EnumerateDirectories(_root))
        {
            string name = Path.GetFileName(directory);

            if (!UserDirectoryName.TryParseUserId(name, out long userId))
            {
                // The incoming directory, or something somebody left here. Not ours to interpret.
                continue;
            }

            if (!users.Contains(userId))
            {
                _logger.LogWarning(
                    "Storage directory {Directory} belongs to user {UserId}, who no longer exists; leaving it alone.",
                    name, userId);

                continue;
            }

            // Through the store rather than by enumerating here, so "what counts as one of a user's
            // files" has one definition - including its handling of a user with two directories.
            found[userId] = [.. _store.List(userId)];
        }

        return found;
    }

    /// <param name="Digest">The content digest, or null when it was not asked for.</param>
    /// <param name="Description">A detached row carrying the metadata columns, or null when they were not asked for.</param>
    /// <param name="Length">The size of the bytes read.</param>
    /// <param name="WrittenAt">The modification time of the bytes read.</param>
    private sealed record FileReading(string? Digest, PrintFile? Description, long Length, DateTimeOffset WrittenAt);
}
