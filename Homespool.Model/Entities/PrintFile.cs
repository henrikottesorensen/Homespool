using System;
using System.Collections.Generic;

namespace Homespool.Model.Entities;

/// <summary>
/// The index row for one uploaded print file. The bytes live on disk under
/// <c>{root}/{userId}/{name}</c>; this exists so that machinery which must outlive a rename has
/// something stable to point at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filesystem is the truth and this is its index</b>, not the other way round
/// (<c>notes/file-storage.md</c>). A file that is on disk is a file the user has, whether or not a row
/// describes it; the two can disagree only across a crash between the write and the commit, and
/// reconciliation is cheap because the tree is enumerable and self-describing. So nothing here is
/// consulted to decide whether a file <i>exists</i> - <c>UserFileStore</c> answers that by looking.
/// </para>
/// <para>
/// <b>Why it exists at all: <see cref="Id"/>.</b> Identity is the name, which is what makes rename a
/// first-class verb rather than a re-upload - and precisely what stops a queue entry or a job record
/// from referencing a file by name. <c>QueuedPrint</c> points at this surrogate, so renaming a queued
/// file breaks nothing. The table was deliberately not built alongside the store on 2026-07-31,
/// because every reader of one was deferred work; the queue is that reader.
/// </para>
/// </remarks>
public class PrintFile
{
    /// <summary>
    /// Surrogate key, and the only thing anything else should reference. Deliberately not the name:
    /// the name is the user's to change.
    /// </summary>
    public long Id { get; set; }

    /// <summary>Whose file this is. With <see cref="Name"/>, the natural key.</summary>
    /// <remarks>
    /// Ownership is really <i>which directory the bytes are in</i>, which is what closed the old
    /// store's "hands a file to whoever holds its id" hole. This column mirrors that so a query can be
    /// scoped without touching the disk; it does not become a second answer to who owns what.
    /// </remarks>
    public long UserId { get; set; }

    /// <summary>
    /// The file's name, which is also its identity to the user and the name it takes on the printer.
    /// </summary>
    /// <remarks>
    /// Unique per user, compared case-insensitively for the reason <c>UserFileStore</c> gives: macOS
    /// folds case and Linux does not, and the printer's FAT32 would collide the two at <c>/usb/</c>
    /// either way.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>Size in bytes, as it was when the row was written.</summary>
    /// <remarks>
    /// A copy of what the filesystem already knows, kept so that listing a user's files does not have
    /// to <c>stat</c> each one. Stale after an out-of-band edit, which the startup reconcile corrects.
    /// </remarks>
    public long Size { get; set; }

    /// <summary>
    /// SHA-384 of the file's content, base64url-encoded. <b>Metadata, not identity</b> - nothing
    /// resolves a file by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nullable, and a null is ordinary.</b> It is computed during the upload's existing streaming
    /// pass, so a file that arrived after this column existed has one; a file already on disk when it
    /// was added does not, and the startup reconcile deliberately does not go and hash the whole store
    /// to fill them in - that would put a full read of every file between the process starting and it
    /// serving, on hardware where that is minutes. Nothing reads this yet, so nothing is blocked by a
    /// null.
    /// </para>
    /// <para>
    /// <b>SHA-384 rather than the SHA-256 <c>notes/file-storage.md</c> sketched</b> (Henrik,
    /// 2026-08-02). Interop was the only argument for 256 and it did not survive checking: PrusaLink's
    /// vendored spec carries no content hash at all, so there is nothing on the other side to compare
    /// against. What is left is that every other hash in this codebase is 384, and that on a CPU with
    /// no SHA acceleration - the Pi 3B's Cortex-A53 - 64-bit words are the friendlier ones.
    /// </para>
    /// <para>
    /// <b>Pinned, not chosen per host.</b> <c>notes/api-tokens.md</c> reaches the same rule for the
    /// token hash and for a different reason - a varying lookup key looks like mass revocation. Here
    /// the reason is dedup: if this column is ever indexed to find identical content, an algorithm
    /// that differs between two hosts silently stops matching. This one is <i>not</i> security-
    /// relevant, and should not inherit that note's reasoning by association.
    /// </para>
    /// </remarks>
    public string? Digest { get; set; }

    /// <summary>
    /// When the bytes were last written, which for an overwrite is when they were replaced.
    /// </summary>
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Queue entries waiting to print this file.</summary>
    /// <remarks>
    /// The reason deleting a file with entries is refused rather than cascaded - see
    /// <see cref="QueuedPrint"/>.
    /// </remarks>
    public virtual ICollection<QueuedPrint> QueuedPrints { get; set; } = [];
}
