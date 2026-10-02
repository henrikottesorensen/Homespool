using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// What one sender decides around <see cref="TransferService"/>'s procedure: whether there is a file
/// to send, whether it may go, and what each answer means. The procedure itself - naming the file on
/// the drive, clearing an older copy, offering it, recording the attempt - is the service's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run on the printer's transfer mailbox, with the procedure's own context</b>, so whatever a
/// policy writes lands in the same save as the procedure's bookkeeping and nothing else touching this
/// printer's transfers can come between them. That is also its one rule: <b>a policy must never call
/// back into <see cref="TransferService"/> for the same printer</b>, which would wait behind itself.
/// </para>
/// <para>
/// Every hook defaults to doing nothing, which is a direct send: the person asked for this file, the
/// answer goes back to them, and nothing is held or counted.
/// </para>
/// </remarks>
public abstract class TransferPolicy
{
    /// <summary>
    /// Whether the attempt is stamped <see cref="PrintFileOnPrinter.TransferStartedAt"/> before the
    /// command goes out - the queue's mark that it is waiting on this transfer, and what makes its
    /// end counted and held. False for a direct send, which nothing waits on.
    /// </summary>
    public virtual bool StampsAttempt => false;

    /// <summary>The bytes to send, or null to send nothing - having recorded why, when that matters.</summary>
    /// <param name="context">The procedure's context.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public abstract Task<StoredFile?> FindFileAsync(TransferContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Answers a file that could not be read to send it. True when handled - the procedure saves and
    /// sends nothing; false, the default, to have the exception reach the caller.
    /// </summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    /// <param name="unreadable">What the store said.</param>
    /// <param name="cancellationToken">Cancels anything the policy asks.</param>
    public virtual Task<bool> UnreadableAsync(TransferContext context,
                                              PrintFileUnreadableException unreadable,
                                              CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    /// <summary>The older copy under the file's name was kept, so nothing is sent. Not saved.</summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    /// <param name="kept">Why the printer kept it.</param>
    /// <param name="cancellationToken">Cancels anything the policy asks.</param>
    public virtual Task CopyKeptAsync(TransferContext context, OutdatedCopyOutcome kept, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The last word before the offer, once any older copy has gone: false to send nothing. Saves
    /// what it records itself, as asking the printer may take a while.
    /// </summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    /// <param name="file">The bytes about to be offered.</param>
    /// <param name="cancellationToken">Cancels anything the policy asks.</param>
    public virtual Task<bool> ReadyToSendAsync(TransferContext context, StoredFile file, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    /// <summary>The printer answered the offer, whatever it said - so the file could be opened. Not saved.</summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    public virtual void Offered(TransferContext context)
    {
    }

    /// <summary>The printer refused the offer. Not saved.</summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    /// <param name="file">The bytes offered.</param>
    /// <param name="digest">Their digest.</param>
    /// <param name="refusal">The printer's answer.</param>
    /// <param name="cancellationToken">Cancels anything the policy asks.</param>
    public virtual Task RefusedAsync(TransferContext context,
                                     StoredFile file,
                                     string digest,
                                     CommandOutcome refusal,
                                     CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>The printer took the transfer; the attempt is already recorded. Not saved.</summary>
    /// <param name="context">The procedure's context; <see cref="TransferContext.Row"/> is set.</param>
    /// <param name="outcome">The printer's answer, or null when the command expects none.</param>
    public virtual void Taken(TransferContext context, CommandOutcome? outcome)
    {
    }
}

/// <summary>
/// What a queued transfer's ending means for the queue: counted, held, or nothing. Resolved per
/// settle, and run on the printer's transfer mailbox inside the settle's own save.
/// </summary>
/// <remarks>
/// Asked only for an attempt the queue stamped (<see cref="TransferPolicy.StampsAttempt"/>), and only
/// for an ending that is not a finish - a finish is arrival, which is the service's to record.
/// </remarks>
public interface ITransferEndPolicy
{
    /// <summary>A queued transfer ended without finishing. Not saved.</summary>
    /// <param name="context">The settle's context; <see cref="TransferContext.Row"/> is the ended attempt's.</param>
    /// <param name="ending"><see cref="PrinterEventType.TransferAborted"/> or <see cref="PrinterEventType.TransferStopped"/>.</param>
    /// <param name="cancellationToken">Cancels anything the policy reads.</param>
    Task EndedAsync(TransferContext context, PrinterEventType ending, CancellationToken cancellationToken);
}

/// <summary>
/// One run of a <see cref="TransferService"/> procedure for one file on one printer: the scope it
/// runs in, and the row it is about.
/// </summary>
public sealed class TransferContext
{
    /// <summary>A run in <paramref name="services"/>' scope, about <paramref name="printFile"/> on <paramref name="printerId"/>.</summary>
    /// <param name="services">The procedure's scope.</param>
    /// <param name="dbContext">The scope's context, which every write in the run goes through.</param>
    /// <param name="printerId">The printer.</param>
    /// <param name="printFile">The file, tracked by <paramref name="dbContext"/>.</param>
    /// <param name="row">The file's row on the printer, or null when there is none yet.</param>
    public TransferContext(IServiceProvider services,
                           HomespoolDbContext dbContext,
                           int printerId,
                           PrintFile printFile,
                           PrintFileOnPrinter? row)
    {
        Services = services;
        DbContext = dbContext;
        PrinterId = printerId;
        PrintFile = printFile;
        Row = row;
    }

    /// <summary>The procedure's scope, for whatever else a policy needs.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The scope's context; every write in the run goes through it.</summary>
    public HomespoolDbContext DbContext { get; }

    /// <summary>The printer.</summary>
    public int PrinterId { get; }

    /// <summary>The file, as the catalogue has it.</summary>
    public PrintFile PrintFile { get; }

    /// <summary>The file's row on the printer, or null until <see cref="EnsureRow"/> has made one.</summary>
    public PrintFileOnPrinter? Row { get; private set; }

    /// <summary>The file's row on the printer, added to the context when there is none. Not saved.</summary>
    public PrintFileOnPrinter EnsureRow()
    {
        if (Row is null)
        {
            Row = new PrintFileOnPrinter { PrinterId = PrinterId, PrintFileId = PrintFile.Id };
            DbContext.PrintFilesOnPrinters.Add(Row);
        }

        return Row;
    }
}

/// <summary>What became of a send through <see cref="TransferService"/>.</summary>
/// <param name="Cleared">What became of an older copy under the file's name, when the procedure got that far.</param>
/// <param name="Sent">What the printer answered, when the file was offered.</param>
public sealed record TransferResult(OutdatedCopyOutcome? Cleared, FileSendResult? Sent);
