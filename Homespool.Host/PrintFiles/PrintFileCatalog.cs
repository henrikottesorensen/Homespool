using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// The store and its index, kept in step: every operation that changes what is on disk does the
/// filesystem half and the row half together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a <c>DbContext</c> inside <see cref="UserFileStore"/>.</b> The store
/// is a singleton and the context is scoped, so one cannot hold the other; and
/// <c>notes/file-storage.md</c> said of the table that "the table joins and this class keeps its
/// shape". It keeps its shape. The store stays a thing that knows about directories, this knows about
/// both, and callers that change files talk to this.
/// </para>
/// <para>
/// <b>The filesystem remains the truth.</b> Nothing here consults a row to decide whether a file
/// exists - <see cref="UserFileStore"/> answers that by looking, as it always did. A row is a handle
/// for things that must outlive a rename, and where the two disagree the disk wins. That is why every
/// read path below can heal a missing row on the spot and why <see cref="PrintFileReconciler"/> can
/// afford to be a plain directory walk.
/// </para>
/// </remarks>
public sealed class PrintFileCatalog
{
    private readonly UserFileStore _store;
    private readonly HSDbContext _dbContext;
    private readonly ILogger<PrintFileCatalog> _logger;

    public PrintFileCatalog(UserFileStore store, HSDbContext dbContext, ILogger<PrintFileCatalog> logger)
    {
        _store = store;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>Everything <paramref name="userId"/> has uploaded. Straight through to the store.</summary>
    public IReadOnlyList<StoredFile> List(long userId)
    {
        return _store.List(userId);
    }

    /// <summary>One of <paramref name="userId"/>'s files by name, or null. Straight through.</summary>
    public StoredFile? Find(long userId, string fileName)
    {
        return _store.Find(userId, fileName);
    }

    /// <summary>
    /// Streams an upload to disk without naming it yet. Straight through - a staged upload has no row
    /// because it is not yet a file anyone has.
    /// </summary>
    public Task<PendingUpload> StageAsync(long userId, string fileName, Stream content,
        CancellationToken cancellationToken)
    {
        return _store.StageAsync(userId, fileName, content, cancellationToken);
    }

    /// <summary>Throws a staged upload away. Straight through, for the same reason.</summary>
    public bool Discard(long userId, string token)
    {
        return _store.Discard(userId, token);
    }

    /// <summary>
    /// The index row for one of <paramref name="userId"/>'s files, creating it if the file is on disk
    /// without one. Null only when there is no such file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what anything wanting to reference a file calls</b> - the queue above all, since a
    /// <see cref="QueuedPrint"/> needs an id rather than a name.
    /// </para>
    /// <para>
    /// <b>It creates the row rather than reporting its absence</b>, because a missing row is not a
    /// user-visible condition: it means the file predates this table, or landed between reconciles.
    /// Refusing to queue a perfectly real file because an index has not caught up would be an
    /// implementation detail surfacing as an error message. The digest is left null - the bytes are
    /// not streaming past here, and reading a whole file to fill a column nothing yet reads is not a
    /// trade worth making.
    /// </para>
    /// </remarks>
    public async Task<PrintFile?> ResolveAsync(long userId, string fileName, CancellationToken cancellationToken)
    {
        StoredFile? file = _store.Find(userId, fileName);

        if (file is null)
        {
            return null;
        }

        PrintFile? row = await FindRowAsync(userId, file.FileName, cancellationToken);

        if (row is not null)
        {
            return row;
        }

        row = Insert(userId, file, digest: null);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Indexed {FileName} for user {UserId}, which had no row", file.FileName, userId);

        return row;
    }

    /// <summary>
    /// Stores an upload and indexes it in one go - the API's upload path.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, or not one a printer would accept.</exception>
    /// <exception cref="PrintFileNameConflictException">The name is taken and <paramref name="overwrite"/> is false.</exception>
    public async Task<StoredFile> SaveAsync(long userId, string fileName, Stream content, bool overwrite,
        CancellationToken cancellationToken, string? userName = null)
    {
        PublishedFile published =
            await _store.SaveAsync(userId, fileName, content, overwrite, cancellationToken, userName);

        await IndexAsync(userId, published, cancellationToken);

        return published.File;
    }

    /// <summary>
    /// Gives a staged upload its name and indexes it, or null if there is no such staged upload for
    /// this user - the Files page's two-step path.
    /// </summary>
    /// <exception cref="PrintFileNameConflictException">The name is taken and <paramref name="overwrite"/> is false.</exception>
    public async Task<StoredFile?> PublishAsync(long userId, string token, bool overwrite,
        CancellationToken cancellationToken, string? userName = null)
    {
        PublishedFile? published = _store.Publish(userId, token, overwrite, userName);

        if (published is null)
        {
            return null;
        }

        await IndexAsync(userId, published, cancellationToken);

        return published.File;
    }

    /// <summary>
    /// Renames a file and its row, or null if there is no such file.
    /// </summary>
    /// <exception cref="ArgumentException">The new name is empty, or not one a printer would accept.</exception>
    /// <exception cref="PrintFileNameConflictException">Another file already has the new name.</exception>
    /// <remarks>
    /// <b>Queued jobs are untouched, and that is the point of the whole table.</b> They reference the
    /// row's id, so the file changing its name is invisible to them.
    /// </remarks>
    public async Task<StoredFile?> RenameAsync(long userId, string fileName, string newName,
        CancellationToken cancellationToken)
    {
        // Resolved before the move, because afterwards the old name finds nothing - and a rename of a
        // file that was never indexed still has to end with a row carrying the new name.
        PrintFile? row = await ResolveAsync(userId, fileName, cancellationToken);

        StoredFile? renamed = _store.Rename(userId, fileName, newName);

        if (renamed is null)
        {
            return null;
        }

        if (row is not null)
        {
            row.Name = renamed.FileName;
            row.UploadedAt = renamed.UploadedAt;

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return renamed;
    }

    /// <summary>
    /// Deletes a file and its row - unless a queued print still wants it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refusing is the deliberate part.</b> Cascading instead would let one person tidying up their
    /// files silently cancel a print somebody else had queued on a shared printer, with the first
    /// symptom being a job that never runs. <c>notes/print-queue.md</c> reaches the same conclusion
    /// about the printer's own copy of a file - delete only when no queued print still wants it - and
    /// the reasoning does not change for ours.
    /// </para>
    /// <para>
    /// The check here is what produces a sentence a person can act on; the foreign key's
    /// <c>Restrict</c> is the backstop for anything that goes around this method.
    /// </para>
    /// </remarks>
    public async Task<PrintFileDeletion> DeleteAsync(long userId, string fileName, CancellationToken cancellationToken)
    {
        StoredFile? file = _store.Find(userId, fileName);

        if (file is null)
        {
            return PrintFileDeletion.NotFound;
        }

        PrintFile? row = await FindRowAsync(userId, file.FileName, cancellationToken);

        if (row is not null)
        {
            int queued = await _dbContext.QueuedPrints
                                         .CountAsync(job => job.PrintFileId == row.Id, cancellationToken);

            if (queued > 0)
            {
                return PrintFileDeletion.Queued;
            }

            _dbContext.PrintFiles.Remove(row);
        }

        // Row first, then bytes. Either order can be interrupted, and both leave a discrepancy the
        // reconcile heals - but this order never leaves a row pointing at a file that is gone, which
        // is the direction a queue entry could act on.
        await _dbContext.SaveChangesAsync(cancellationToken);

        return _store.Delete(userId, fileName) ? PrintFileDeletion.Deleted : PrintFileDeletion.NotFound;
    }

    /// <summary>Writes or refreshes the row for a file that has just been published.</summary>
    private async Task IndexAsync(long userId, PublishedFile published, CancellationToken cancellationToken)
    {
        PrintFile? row = await FindRowAsync(userId, published.File.FileName, cancellationToken);

        if (row is null)
        {
            Insert(userId, published.File, published.Digest);
        }
        else
        {
            // An overwrite: same name, same row, different bytes. Keeping the row is what lets a
            // queued print print the replacement, which is the behaviour "overwrite" promises.
            row.Name = published.File.FileName;
            row.Size = published.File.Length;
            row.Digest = published.Digest;
            row.UploadedAt = published.File.UploadedAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private PrintFile Insert(long userId, StoredFile file, string? digest)
    {
        PrintFile row = new()
        {
            UserId = userId,
            Name = file.FileName,
            Size = file.Length,
            Digest = digest,
            UploadedAt = file.UploadedAt,
        };

        _dbContext.PrintFiles.Add(row);

        return row;
    }

    /// <summary>
    /// The row for a name, matched the way the store matches names.
    /// </summary>
    /// <remarks>
    /// The equality below is case-insensitive because the column carries SQLite's <c>NOCASE</c>
    /// collation, not because of anything in this expression - which is the point of putting the
    /// collation on the column rather than at each call site, since <c>OrdinalIgnoreCase</c> has no
    /// translation and a query that quietly became case-sensitive would duplicate rows.
    /// </remarks>
    private Task<PrintFile?> FindRowAsync(long userId, string fileName, CancellationToken cancellationToken)
    {
        return _dbContext.PrintFiles
                         .SingleOrDefaultAsync(f => f.UserId == userId && f.Name == fileName, cancellationToken);
    }
}

/// <summary>What became of a delete.</summary>
public enum PrintFileDeletion
{
    /// <summary>There was no such file.</summary>
    NotFound = 0,

    /// <summary>The file and its row are gone.</summary>
    Deleted,

    /// <summary>Refused: a queued print still wants this file.</summary>
    Queued,
}
