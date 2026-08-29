using System;
using System.Collections.Generic;
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
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Brings the file index back into agreement with the disk, once, at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filesystem is the truth, so this only ever teaches the table</b> - it adds rows for files it
/// finds, corrects sizes and timestamps that moved, and removes rows for files that are gone. It never
/// creates, renames or deletes a file to match a row. That direction is what makes hand-managing the
/// data directory a supported thing to do rather than a way to corrupt state.
/// </para>
/// <para>
/// <b>It does not hash anything.</b> Filling in the digests of files that predate that column would
/// mean a full read of every file in the store between the process starting and it serving requests -
/// minutes on the hardware this is expected to run on, for a column nothing reads yet
/// (<see cref="PrintFile.Digest"/>). Uploads compute theirs on the pass they already make; everything
/// older keeps a null until something needs otherwise.
/// </para>
/// <para>
/// <b>Once, not on a timer.</b> Every path that changes a file goes through
/// <see cref="PrintFileCatalog"/> and keeps both halves in step, and the read paths heal a missing row
/// on the spot. So this is for what happened while the process was <i>not</i> running - a restore, a
/// hand-copied file, an interrupted write - which is a startup-shaped problem.
/// </para>
/// </remarks>
public sealed class PrintFileReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserFileStore _store;
    private readonly string _root;
    private readonly ILogger<PrintFileReconciler> _logger;

    public PrintFileReconciler(IServiceScopeFactory scopeFactory,
                               UserFileStore store,
                               IOptionsMonitor<PrintFileStorageOptions> options,
                               IHostEnvironmentAccessor environment,
                               ILogger<PrintFileReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _scopeFactory = scopeFactory;
        _store = store;
        _root = Path.IsPathRooted(options.CurrentValue.Directory) ?
            options.CurrentValue.Directory :
            Path.Combine(environment.ContentRootPath, options.CurrentValue.Directory);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the walk finished. Nothing is half-applied that matters - the next
            // start runs it again, and the read paths heal whatever it did not reach.
        }
        catch (Exception e)
        {
            // A stale index is a far smaller problem than a service that will not start, and every
            // reader can cope with one: a missing row is created on demand, and an extra row is only
            // reachable through a file that is not there.
            _logger.LogError(e, "Reconciling the print-file index failed; it may be out of date.");
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
                    });

                    added++;

                    continue;
                }

                if (row.Size != file.Length || row.UploadedAt != file.UploadedAt)
                {
                    // The bytes changed underneath us. The digest is now a statement about content
                    // that is gone, so it is cleared rather than left to be believed.
                    row.Size = file.Length;
                    row.UploadedAt = file.UploadedAt;
                    row.Digest = null;

                    corrected++;
                }
            }
        }

        foreach (PrintFile row in rows)
        {
            if (onDisk.TryGetValue(row.UserId, out List<StoredFile>? files)
                && files.Exists(file => string.Equals(file.FileName, row.Name, StringComparison.OrdinalIgnoreCase)))
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
                    "{FileName} (user {UserId}) is gone from disk but {Count} queued print(s) referenced it; "
                    + "cancelling them - the file was removed outside Homespool.",
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
}
