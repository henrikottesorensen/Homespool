using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles.GCode;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// The store and its index, kept in step: every operation that changes what is on disk does the
/// filesystem half and the row half together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a <c>DbContext</c> inside <see cref="UserFileStore"/>.</b> The store
/// is a singleton and the context is scoped, so one cannot hold the other. The table joins and the
/// store keeps its shape: the store stays a thing that knows about directories, this knows about
/// both, and callers that change files talk to this.
/// </para>
/// <para>
/// <b>The filesystem remains the truth.</b> Nothing here consults a row to decide whether a file
/// exists - <see cref="UserFileStore"/> answers that by looking, as it always did. A row is a handle
/// for things that must outlive a rename, and where the two disagree the disk wins. That is why every
/// read path below can heal a missing row on the spot and why <see cref="PrintFileReconciler"/> can
/// afford to be a plain directory walk.
/// </para>
/// <para>
/// <b>The credential gate is here rather than in the controllers and pages</b>, on the same argument
/// as <see cref="Authorisation.PrinterAccessService"/>: a rule living with the callers is one the next
/// caller forgets. Every entry point below opens with
/// <see cref="Authorisation.CredentialScope.Require"/> and asks nothing further, because a file has no
/// team - it lives at <c>{userId}/{name}</c>, so nobody else can reach it and its owner cannot be
/// refused it. The only question is which of their own powers this key was given.
/// </para>
/// </remarks>
public sealed class PrintFileCatalog
{
    private readonly UserFileStore _store;
    private readonly HomespoolDbContext _dbContext;
    private readonly ILogger<PrintFileCatalog> _logger;

    public PrintFileCatalog(UserFileStore store, HomespoolDbContext dbContext, ILogger<PrintFileCatalog> logger)
    {
        _store = store;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>Everything the caller has uploaded. Straight through to the store.</summary>
    public IReadOnlyList<StoredFile> List(Caller caller)
    {
        CredentialScope.Require(caller, Capability.ViewOwnFiles);

        return _store.List(caller.UserId);
    }

    /// <summary>
    /// Everything the caller has uploaded, each with its row where it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The disk decides what is listed; the rows only describe it.</b> A file with no row is still
    /// the caller's file - the startup reconcile or the next print will index it - so it is listed
    /// with nothing known about it rather than left out.
    /// </para>
    /// <para>
    /// One query for all of the caller's rows, each file matched to its row by
    /// <see cref="BestMatch{T}"/> - the store's rule, applied here rather than asked of the database.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CataloguedFile>> ListAsync(Caller caller, CancellationToken cancellationToken)
    {
        CredentialScope.Require(caller, Capability.ViewOwnFiles);

        IReadOnlyList<StoredFile> files = _store.List(caller.UserId);

        List<HSFile> rows = await _dbContext.Files
                                            .AsNoTracking()
                                            .Where(row => row.UserId == caller.UserId)
                                            .OrderBy(row => row.Id)
                                            .ToListAsync(cancellationToken);

        return [.. files.Select(file => new CataloguedFile(file, BestMatch(rows, row => row.Name, file.FileName)))];
    }

    /// <summary>
    /// The row describing a file the caller already holds, or null when it has none.
    /// </summary>
    /// <remarks>
    /// <b>Authorises nothing, and takes a <see cref="StoredFile"/> so that it need not.</b> One is only
    /// in hand after a call that was gated - an upload, a rename - and this answers what that call
    /// just wrote. Requiring <see cref="Capability.ViewOwnFiles"/> here would refuse a token scoped to
    /// upload the description of the file it has just uploaded.
    /// </remarks>
    public Task<HSFile?> RowForAsync(long userId, StoredFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        return FindRowAsync(userId, file.FileName, cancellationToken);
    }

    /// <summary>One of the caller's files by name, or null. Straight through.</summary>
    public StoredFile? Find(Caller caller, string fileName)
    {
        CredentialScope.Require(caller, Capability.ViewOwnFiles);

        return _store.Find(caller.UserId, fileName);
    }

    /// <summary>
    /// One of the caller's files, resolved so it can be <i>printed</i> rather than read.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not gated on <see cref="Capability.ViewOwnFiles"/>.</b> Sending a file to a
    /// printer and queueing one are both <see cref="Capability.Print"/>, checked before this is
    /// reached, and the resolve is how that act finds its bytes - not a second, browsing-shaped
    /// permission. Requiring the view here would mean a token scoped to print could not print.
    /// <para>
    /// Takes a bare id because it decides nothing: the deciding was done by whoever is about to
    /// print.
    /// </para>
    /// </remarks>
    public StoredFile? FindForPrinting(long userId, string fileName)
    {
        return _store.Find(userId, fileName);
    }

    /// <summary>
    /// Whether the user's storage is there at all, so a file <see cref="FindForPrinting"/> missed can
    /// be told apart from storage that did not come up. Straight through.
    /// </summary>
    public bool HasStorageFor(long userId)
    {
        return _store.HasDirectory(userId);
    }

    /// <summary>
    /// Streams an upload to disk without naming it yet. Straight through - a staged upload has no row
    /// because it is not yet a file anyone has.
    /// </summary>
    public async Task<PendingUpload> StageAsync(Caller caller,
                                                string fileName,
                                                Stream content,
                                                CancellationToken cancellationToken,
                                                bool refuseTakenName = false)
    {
        CredentialScope.Require(caller, Capability.UploadOwnFiles);

        await ConfirmFreshStorageAsync(cancellationToken);

        return await _store.StageAsync(caller.UserId, fileName, content, cancellationToken, refuseTakenName);
    }

    /// <summary>
    /// Marks the storage as the real one when nothing is indexed, because then there is nothing an
    /// empty root could be hiding.
    /// </summary>
    /// <remarks>
    /// An install with rows is never marked here: rows and a root with no marker are what an
    /// unmounted volume looks like, so its operator creates the marker once they have seen the
    /// right disk is there.
    /// </remarks>
    private async Task ConfirmFreshStorageAsync(CancellationToken cancellationToken)
    {
        if (!_store.IsConfirmed && !await _dbContext.Files.AnyAsync(cancellationToken))
        {
            _store.Confirm();
        }
    }

    /// <summary>Throws a staged upload away. Straight through, for the same reason.</summary>
    public bool Discard(Caller caller, string token)
    {
        // Throwing away your own half-finished upload is part of uploading, not manipulation of a
        // file that exists - nothing is published under a name yet.
        CredentialScope.Require(caller, Capability.UploadOwnFiles);

        return _store.Discard(caller.UserId, token);
    }

    /// <summary>
    /// The index row for one of the caller's files, creating it if the file is on disk
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
    /// not streaming past here, and reading a whole file would stand in front of whoever is printing
    /// it. The reconciler's background pass fills it in, within one recheck interval.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Takes a bare id because it decides nothing.</b> Its callers - enqueueing, and renaming - have
    /// already been gated by the capability their own act needs, and a second, browsing-shaped check
    /// here would mean a credential scoped to print could not print.
    /// </remarks>
    public async Task<HSFile?> ResolveAsync(long userId, string fileName, CancellationToken cancellationToken)
    {
        StoredFile? file = _store.Find(userId, fileName);

        if (file is null)
        {
            return null;
        }

        HSFile? row = await FindRowAsync(userId, file.FileName, cancellationToken);

        if (row is not null)
        {
            if (!string.Equals(row.Name, file.FileName, StringComparison.Ordinal))
            {
                // Renamed on disk to another case since the row was written. The row carries the
                // disk's spelling, which is the one the printer is sent.
                row.Name = file.FileName;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return row;
        }

        row = Insert(userId, file, digest: null);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Indexed {FileName} for user {UserId}, which had no row", file.FileName, userId);

        return row;
    }

    /// <summary>
    /// The digest of the bytes about to be sent to a printer: the row's, or read from the file now
    /// when the row has none yet.
    /// </summary>
    /// <exception cref="PrintFileUnreadableException">The file could not be read.</exception>
    /// <remarks>
    /// <para>
    /// <b>No file is sent without one</b>, because the digest is what a printer's copy is later told
    /// apart from a newer version by. Waiting for the reconciler's backfill would hold a print for up
    /// to a recheck interval, so the send reads the file itself - once, since the row keeps it.
    /// </para>
    /// <para>
    /// <b>Called before the file is opened for the offer, and the order is the safe one.</b> An
    /// overwrite landing between the two sends newer bytes than this digest describes, which only
    /// makes the copy look outdated and costs one more transfer; the other order could record a new
    /// digest against old bytes, which is the defect this exists to prevent.
    /// </para>
    /// <para>
    /// <b>Written to the row only while the row still describes the bytes that were read</b> - the
    /// reconciler's backfill condition, for the same reason: an upload replacing the file mid-read
    /// writes its own digest, and this must not overwrite it. <paramref name="row"/> itself is not
    /// changed, so a caller holding it tracked does not write the value back unconditionally.
    /// Takes no caller because it decides nothing; whoever is sending was gated already.
    /// </para>
    /// </remarks>
    public async Task<string> DigestForSendingAsync(HSFile row, StoredFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(file);

        if (row.Digest is not null)
        {
            return row.Digest;
        }

        string digest;
        long length;
        DateTimeOffset writtenAt;

        try
        {
            // Shared for writing and deleting, as the reconciler reads: an upload replacing the file
            // goes ahead, and the conditional write below sorts it out.
            await using FileStream stream = new(file.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });

            digest = await PrintFileDigest.ComputeAsync(stream, copyTo: null, cancellationToken);

            // From the handle, after the read: the path may already name a replacement.
            length = RandomAccess.GetLength(stream.SafeFileHandle);
            writtenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read {FileName} (user {UserId}) for its digest", row.Name, row.UserId);

            throw new PrintFileUnreadableException(file.FileName);
        }

        await _dbContext.Files
                        .Where(candidate => candidate.Id == row.Id &&
                                            candidate.Digest == null &&
                                            candidate.Size == length &&
                                            candidate.UploadedAt == writtenAt)
                        .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.Digest, digest),
                                            cancellationToken);

        _logger.LogInformation("Read {FileName} (user {UserId}) for its digest before sending it", row.Name, row.UserId);

        return digest;
    }

    /// <summary>
    /// Stores an upload and indexes it in one go - the API's upload path.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, or not one a printer would accept.</exception>
    /// <exception cref="PrintFileNameConflictException">The name is taken and <paramref name="overwrite"/> is false.</exception>
    public async Task<StoredFile> SaveAsync(Caller caller,
                                            string fileName,
                                            Stream content,
                                            bool overwrite,
                                            CancellationToken cancellationToken,
                                            string? userName = null)
    {
        CredentialScope.Require(caller, RequiredToWrite(overwrite));

        await ConfirmFreshStorageAsync(cancellationToken);

        PublishedFile published =
            await _store.SaveAsync(caller.UserId, fileName, content, overwrite, cancellationToken, userName);

        await IndexAsync(caller.UserId, published, cancellationToken);

        return published.File;
    }

    /// <summary>
    /// What a write needs: <see cref="Capability.UploadOwnFiles"/> for a new name,
    /// <see cref="Capability.ManipulateOwnFiles"/> when it asks to overwrite.
    /// </summary>
    /// <remarks>
    /// <b>Overwriting is manipulation whatever the verb says</b> - it destroys bytes under a name
    /// already in use - and <i>upload own files</i> must not sound like it does that. The flag
    /// decides, not the name: a credential holding only the upload capability is refused any
    /// overwrite as a <c>403</c> before the name is looked at, and without the flag an existing name
    /// is the <c>409</c> every caller gets.
    /// </remarks>
    private static Capability RequiredToWrite(bool overwrite)
    {
        return overwrite ? Capability.ManipulateOwnFiles : Capability.UploadOwnFiles;
    }

    /// <summary>
    /// Gives a staged upload its name and indexes it, or null if there is no such staged upload for
    /// this user - the Files page's two-step path.
    /// </summary>
    /// <exception cref="PrintFileNameConflictException">The name is taken and <paramref name="overwrite"/> is false.</exception>
    public async Task<StoredFile?> PublishAsync(Caller caller,
                                                string token,
                                                bool overwrite,
                                                CancellationToken cancellationToken,
                                                string? userName = null)
    {
        CredentialScope.Require(caller, RequiredToWrite(overwrite));

        PublishedFile? published = _store.Publish(caller.UserId, token, overwrite, userName);

        if (published is null)
        {
            return null;
        }

        await IndexAsync(caller.UserId, published, cancellationToken);

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
    public async Task<StoredFile?> RenameAsync(Caller caller,
                                               string fileName,
                                               string newName,
                                               CancellationToken cancellationToken)
    {
        CredentialScope.Require(caller, Capability.ManipulateOwnFiles);

        long userId = caller.UserId;

        // Resolved before the move, because afterwards the old name finds nothing - and a rename of a
        // file that was never indexed still has to end with a row carrying the new name.
        HSFile? row = await ResolveAsync(userId, fileName, cancellationToken);

        StoredFile? renamed = _store.Rename(userId, fileName, newName);

        if (renamed is null)
        {
            return null;
        }

        if (row is not null)
        {
            row.Name = renamed.FileName;
            row.UploadedAt = renamed.UploadedAt;

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent publish indexed a row under the new name between the move and this
                // write, and the unique (user, name) index refused ours. The rename itself succeeded
                // on disk, which is the truth: leave the old row as it was and let the reconcile heal
                // the pair rather than answering a completed rename with a 500.
                _dbContext.Entry(row).State = EntityState.Detached;

                _logger.LogWarning("Renamed {FileName} to {NewName} for user {UserId}, but a row for the new name already existed; leaving the index to the reconcile",
                                   fileName, renamed.FileName, userId);
            }
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
    /// symptom being a job that never runs. The printer's own copy of a file follows the same rule
    /// - delete only when no queued print still wants it - and
    /// the reasoning does not change for ours.
    /// </para>
    /// <para>
    /// The check here is what produces a sentence a person can act on; the foreign key's
    /// <c>Restrict</c> is the backstop for anything that goes around this method.
    /// </para>
    /// </remarks>
    public async Task<PrintFileDeletion> DeleteAsync(Caller caller, string fileName, CancellationToken cancellationToken)
    {
        CredentialScope.Require(caller, Capability.ManipulateOwnFiles);

        long userId = caller.UserId;

        StoredFile? file = _store.Find(userId, fileName);

        if (file is null)
        {
            return PrintFileDeletion.NotFound;
        }

        HSFile? row = await FindRowAsync(userId, file.FileName, cancellationToken);

        if (row is not null)
        {
            int queued = await _dbContext.QueuedPrints
                                         .CountAsync(job => job.FileId == row.Id, cancellationToken);

            if (queued > 0)
            {
                return PrintFileDeletion.Queued;
            }

            _dbContext.Files.Remove(row);
        }

        // Row first, then bytes. Either order can be interrupted, and both leave a discrepancy the
        // reconcile heals - but this order never leaves a row pointing at a file that is gone, which
        // is the direction a queue entry could act on.
        await _dbContext.SaveChangesAsync(cancellationToken);

        return _store.Delete(userId, fileName) ? PrintFileDeletion.Deleted : PrintFileDeletion.NotFound;
    }

    /// <summary>Writes or refreshes the row for a file that has just been published.</summary>
    /// <remarks>
    /// <b>Where the file's own account of itself is read</b>, because it is the one place both
    /// upload paths meet and the bytes have just landed on local disk. It reads a header and a tail
    /// rather than the whole file, so the cost does not scale with the upload.
    /// </remarks>
    private async Task IndexAsync(long userId, PublishedFile published, CancellationToken cancellationToken)
    {
        HSFile? row = await FindRowAsync(userId, published.File.FileName, cancellationToken);
        bool inserted = row is null;

        row ??= Insert(userId, published.File, published.Digest);

        Apply(row, published);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (inserted)
        {
            // A concurrent publish of the same name indexed it between our lookup and our insert, and
            // the unique (user, name) index refused the second row. Not an error: the file exists once,
            // so drop our insert and write onto the row that won.
            _dbContext.Entry(row).State = EntityState.Detached;

            HSFile winner = await FindRowAsync(userId, published.File.FileName, cancellationToken) ??
                            throw new InvalidOperationException(
                                $"Indexing {published.File.FileName} failed on a duplicate row that then could not be found.");

            Apply(winner, published);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private void Apply(HSFile row, PublishedFile published)
    {
        // An overwrite reaches here with the existing row: same name, same row, different bytes.
        // Keeping the row is what lets a queued print print the replacement, which is the behaviour
        // "overwrite" promises. A printer still holding the old bytes is not touched here: its copy
        // carries the digest it was sent with, and the new one below is what marks it outdated. It
        // is also why the metadata below must be rewritten rather than filled only when absent,
        // since the new bytes may have been sliced for something else.
        row.Name = published.File.FileName;
        row.Size = published.File.Length;
        row.Digest = published.Digest;
        row.UploadedAt = published.File.UploadedAt;

        Describe(row, published.File.Path);
    }

    /// <summary>
    /// Reads what the file says it was sliced for onto <paramref name="row"/>.
    /// </summary>
    /// <remarks>
    /// <b>An unreadable file is recorded, never thrown.</b> Somebody uploading something corrupt has
    /// still uploaded a file - they own the bytes, the store holds them, and refusing the upload
    /// over a parse would be this check deciding what may exist rather than what may be printed.
    /// </remarks>
    private void Describe(HSFile row, string path)
    {
        GCodeMetadata? metadata = GCodeMetadataReader.ReadFile(path);

        if (metadata is null)
        {
            _logger.LogInformation("Could not read print metadata from {FileName}", row.Name);
        }

        PrintFileMetadata.Apply(row, metadata);
    }

    private HSFile Insert(long userId, StoredFile file, string? digest)
    {
        HSFile row = new()
        {
            UserId = userId,
            Name = file.FileName,
            Size = file.Length,
            Digest = digest,
            UploadedAt = file.UploadedAt,

            // True until the bytes are read, which an upload does next and a lazy resolve leaves to
            // the reconciler's background pass - so a row indexed on the way to a print says nobody
            // has looked, rather than carrying the default that means nobody wrote a state at all.
            MetadataState = PrintFileMetadataState.Unread,
        };

        _dbContext.Files.Add(row);

        return row;
    }

    /// <summary>
    /// The row for a file the store has resolved, matched by the store's rule for names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The database is never asked whether two names are the same.</b> The store decides that with
    /// <c>OrdinalIgnoreCase</c>, across all of Unicode; the column's <c>NOCASE</c> folds ASCII only, so
    /// a lookup the database answered missed <c>ærø</c> for <c>Ærø</c> and an overwrite inserted a
    /// second row for the one file. No collation SQLite offers agrees with .NET's, so the user's names
    /// are read and matched here - tens to hundreds of short strings - and the row is then fetched by
    /// the exact spelling it holds, which every collation agrees on.
    /// </para>
    /// </remarks>
    private async Task<HSFile?> FindRowAsync(long userId, string fileName, CancellationToken cancellationToken)
    {
        List<string> names = await _dbContext.Files
                                             .Where(row => row.UserId == userId)
                                             .OrderBy(row => row.Id)
                                             .Select(row => row.Name)
                                             .ToListAsync(cancellationToken);

        string? name = BestMatch(names, candidate => candidate, fileName);

        return name is null ?
            null :
            await _dbContext.Files.SingleAsync(row => row.UserId == userId && row.Name == name, cancellationToken);
    }

    /// <summary>
    /// The item whose name is <paramref name="fileName"/> by the store's rule: the exact spelling if
    /// one has it, otherwise the first that differs only in case, otherwise none.
    /// </summary>
    /// <remarks>
    /// Exact first, because two items can answer only in a database holding two rows for one file,
    /// written before rows were matched this way - and of those, the one spelled as the disk is the
    /// one that is right.
    /// </remarks>
    private static T? BestMatch<T>(IEnumerable<T> items, Func<T, string> nameOf, string fileName)
        where T : class
    {
        T? folded = null;

        foreach (T item in items)
        {
            string name = nameOf(item);

            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return item;
            }

            if (folded is null && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                folded = item;
            }
        }

        return folded;
    }
}
