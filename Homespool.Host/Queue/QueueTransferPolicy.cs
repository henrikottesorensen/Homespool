using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// What the queue decides around a send of its head's file: whether there is a file to send, whether
/// the drive has room, and what each refusal means - holds, counts, and the history row a hold leaves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run by <see cref="TransferService"/> on the printer's transfer mailbox</b>, so a hold or a count
/// lands in the same save as the attempt it is about, and a direct send of the same file cannot come
/// between them.
/// </para>
/// <para>
/// <b>The queue's attempts are stamped</b> (<see cref="StampsAttempt"/>), which is what makes the
/// snapshot wait on them and what makes their end count against the file.
/// </para>
/// </remarks>
internal sealed class QueueTransferPolicy : TransferPolicy
{
    /// <summary>
    /// Firmware's code for "the drive already has that name" - observed on an MK3.5 at 6.5.7+12836,
    /// answering <c>START_CONNECT_DOWNLOAD</c> with <c>"File already exists"</c> beside it.
    /// </summary>
    /// <remarks>
    /// Its own code rather than a <c>STORAGE_FAILURE</c>, which is what makes this case separable:
    /// firmware distinguishes "it is already there" from "storage went wrong", so the loop can too.
    /// </remarks>
    private const string FileExistsCode = "FILE_EXISTS";

    private readonly long _headId;
    private readonly QueueHolds _holds;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private QueuedPrint? _head;

    /// <summary>The queue's decisions for a send of <paramref name="headId"/>'s file.</summary>
    /// <param name="headId">The queue entry whose file is sent.</param>
    /// <param name="timeProvider">The loop's clock.</param>
    /// <param name="logger">The advancer's log.</param>
    public QueueTransferPolicy(long headId, TimeProvider timeProvider, ILogger logger)
    {
        _headId = headId;
        _timeProvider = timeProvider;
        _logger = logger;
        _holds = new QueueHolds(timeProvider, logger);
    }

    /// <inheritdoc />
    public override bool StampsAttempt => true;

    /// <summary>
    /// Whether the service let this send begin - false when it was refused because another send to the
    /// printer was waiting or under way.
    /// </summary>
    public bool Begun { get; private set; }

    /// <summary>
    /// Whether the send got past the older copy and the free-space question to the offer itself -
    /// which tells a failure to send the file from a failure to clear its way.
    /// </summary>
    public bool ReachedTheOffer { get; private set; }

    private QueuedPrint Head => _head ?? throw new InvalidOperationException("The entry is read before anything else is decided.");

    /// <inheritdoc />
    /// <remarks>
    /// <b>The entry is read again here</b>, on the printer's transfer mailbox: it may have been
    /// cancelled since the pass decided to send it, and then there is nothing to send.
    /// </remarks>
    public override async Task<StoredFile?> FindFileAsync(TransferContext context, CancellationToken cancellationToken)
    {
        Begun = true;
        _head = await context.DbContext.QueuedPrints
                                       .Include(queued => queued.PrintFile)
                                       .SingleOrDefaultAsync(queued => queued.Id == _headId, cancellationToken);

        if (_head is null)
        {
            return null;
        }

        PrintFileCatalog catalog = context.Services.GetRequiredService<PrintFileCatalog>();
        StoredFile? file = catalog.FindForPrinting(_head.QueuedByUserId, _head.PrintFile!.Name);

        if (file is null && !catalog.HasStorageFor(_head.QueuedByUserId))
        {
            // Not a file that went: the owner's whole directory is missing, which no delete leaves
            // behind - an unmounted volume, an empty mount point. Dropping would cancel every entry
            // in turn on a disk that is not there, so it is held like a file that cannot be opened,
            // and the first pass that finds the storage back sends it.
            bool existed = context.Row is not null;
            PrintFileOnPrinter onPrinter = context.EnsureRow();

            if (existed &&
                onPrinter is { HoldReason: PrintHoldReason.FileUnreadable, BlockedAt: { } blockedAt } &&
                _timeProvider.GetUtcNow() - blockedAt < QueueAdvancer.BlockRecheckAfter)
            {
                // Asked recently enough, as the space hold is: re-holding every tick would write a row
                // every few seconds for as long as the storage stays away.
                return null;
            }

            _holds.HoldUnreadable(context.DbContext, context.PrinterId, _head, onPrinter, null);

            return null;
        }

        if (file is null)
        {
            // The bytes went while the entry waited. Nothing to send and nothing to wait for, so the
            // entry is dropped rather than retried forever - the reconciler makes the same call when
            // it finds a row whose file has left.
            _logger.LogWarning("[{PrinterId}] {FileName} is queued but no longer on disk; dropping the entry",
                               context.PrinterId, _head.PrintFile.Name);
            context.DbContext.QueuedPrints.Remove(_head);
        }

        return file;
    }

    /// <inheritdoc />
    /// <remarks>Gone, or held until it can be read again.</remarks>
    public override Task<bool> UnreadableAsync(TransferContext context,
                                               PrintFileUnreadableException unreadable,
                                               CancellationToken cancellationToken)
    {
        if (context.Services.GetRequiredService<PrintFileCatalog>().FindForPrinting(Head.QueuedByUserId, Head.PrintFile!.Name) is null)
        {
            // Deleted between being found and being opened. The next pass finds it missing and
            // drops the entry, as it would have had the delete come a moment sooner.
            _logger.LogInformation(unreadable, "[{PrinterId}] {FileName} went while it was being sent",
                                   context.PrinterId, Head.PrintFile.Name);
        }
        else
        {
            _holds.HoldUnreadable(context.DbContext, context.PrinterId, Head, context.Row!, unreadable);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>A copy in use is waited for, not counted</b>: a print of it ends and a transfer of it finishes,
    /// as a busy transfer slot frees up, and like the slot it is asked about again on the next pass. Any
    /// other refusal is counted as a refused transfer - the file cannot be sent while the copy is there -
    /// so a printer that will never delete it holds the queue with its own words after the same bound.
    /// </remarks>
    public override Task CopyKeptAsync(TransferContext context, OutdatedCopyOutcome kept, CancellationToken cancellationToken)
    {
        if (kept.Removal == OutdatedCopyRemoval.InUse)
        {
            _logger.LogDebug("[{PrinterId}] the older copy of {FileName} is in use; waiting to replace it",
                             context.PrinterId, Head.PrintFile!.Name);
        }
        else
        {
            _holds.RecordRefusal(context.DbContext, context.PrinterId, Head, context.Row!, code: null, kept.Reason,
                                 PrintHoldReason.TransferRefused);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>After the older copy has gone</b>, because deleting it is what may make room.
    /// </remarks>
    public override async Task<bool> ReadyToSendAsync(TransferContext context, StoredFile file, CancellationToken cancellationToken)
    {
        ReachedTheOffer = await HasRoomForAsync(context.Services, context.DbContext, context.PrinterId, Head, file.Length,
                                                context.Row!, cancellationToken);

        return ReachedTheOffer;
    }

    /// <inheritdoc />
    public override void Offered(TransferContext context)
    {
        // Whatever the printer said, the file was opened to offer it, which is all this hold was
        // about.
        if (context.Row!.HoldReason == PrintHoldReason.FileUnreadable)
        {
            _logger.LogInformation("[{PrinterId}] {FileName} can be read again; the queue resumes",
                                   context.PrinterId, Head.PrintFile!.Name);

            QueueHolds.ClearHold(context.Row);
        }
    }

    /// <inheritdoc />
    public override async Task RefusedAsync(TransferContext context,
                                            StoredFile file,
                                            string digest,
                                            CommandOutcome refusal,
                                            CancellationToken cancellationToken)
    {
        PrintFileOnPrinter onPrinter = context.Row!;

        // Classified on MachineReason, not on the prose: the code is a fixed vocabulary and the
        // wording is free to change between releases. Both are the printer's text, so both are
        // cleaned before they reach a log line.
        _logger.LogInformation(
            "[{PrinterId}] refused the transfer of {FileName}: {Reason} [{MachineReason}]",
            context.PrinterId, file.FileName, LogText.Clean(refusal.Reason), LogText.Clean(refusal.MachineReason));

        if (refusal.MachineReason == FileExistsCode)
        {
            TransferRetryRules.Forget(onPrinter);
            await ReconcileExistingFileAsync(context.Services, context.PrinterId, Head, file, digest, onPrinter, cancellationToken);
        }
        else if (!TransferRetryRules.IsBusySlot(refusal.MachineReason, refusal.Reason))
        {
            _holds.RecordRefusal(context.DbContext, context.PrinterId, Head, onPrinter, refusal.MachineReason, refusal.Reason,
                                 PrintHoldReason.TransferRefused);
        }
    }

    /// <inheritdoc />
    public override void Taken(TransferContext context, CommandOutcome? outcome)
    {
        PrintFileOnPrinter onPrinter = context.Row!;

        if (outcome is not null && onPrinter.TransferRefusalCount is not null &&
            !TransferRetryRules.IsCountingAborts(onPrinter))
        {
            // Whatever was refusing it has stopped. Only on an answer: a null outcome is the absence
            // of one, which says nothing about the refusals before it. Not a count of aborts, which
            // every aborted attempt is taken before it fails - that one ends when a transfer finishes.
            TransferRetryRules.Forget(onPrinter);
        }
    }

    /// <summary>
    /// Answers a <c>FILE_EXISTS</c> refusal by asking what is actually on the drive: adopts a copy of
    /// these very bytes that Homespool sent, and sends them under the next name otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refusal is sometimes a cache miss in our own bookkeeping.</b> A transfer whose end went
    /// unseen - trimmed from the event log before it was read, or running when its command's id could
    /// not be recorded - keeps the digest it was sent with and never arrives, so a name refused here
    /// may hold exactly the bytes we wanted to send, and the answer is to record that they arrived and
    /// print. That is the same conclusion
    /// <c>File not found</c> reaches from the other direction: the drive is the truth.
    /// </para>
    /// <para>
    /// <b>Only when <see cref="PrintFileOnPrinter.Digest"/> says so.</b> <c>FILE_INFO</c> carries no
    /// digest, so what is on the drive can only be vouched for by what Homespool recorded sending
    /// there. A matching size is checked as well, but never on its own: a re-slice that changes one
    /// temperature keeps its length, so a size match would adopt the older version of a file and print
    /// it under the newer one's name. A file nobody recorded - put there by PrusaLink, a USB stick, or
    /// a transfer from before digests were kept - is treated as somebody else's, at the cost of one
    /// copy under another name.
    /// </para>
    /// <para>
    /// <b>Our own copy still read-only is waited for</b> - a transfer of it still arriving, or a print
    /// of it running - and asked about again on the next pass, as a busy transfer slot is.
    /// </para>
    /// <para>
    /// <b>Anything else goes on under the next name</b> - a stranger's file, a different size, none
    /// reported. Holding until somebody clears the drive would stop a queue over a stranger's file,
    /// and the stranger's file is not ours to delete. Only when every name is taken does the queue hold.
    /// </para>
    /// <para>
    /// <b>The path recorded is the one <c>FILE_INFO</c> answers with</b>, not the one we asked about:
    /// that is the 8.3 alias, which is what <c>START_PRINT</c> then uses, and it is unguessable from
    /// here because the counter depends on what else is on that drive.
    /// </para>
    /// </remarks>
    private async Task ReconcileExistingFileAsync(IServiceProvider services,
                                                  int printerId,
                                                  QueuedPrint head,
                                                  StoredFile file,
                                                  string digest,
                                                  PrintFileOnPrinter onPrinter,
                                                  CancellationToken cancellationToken)
    {
        PrinterCommandService commands = services.GetRequiredService<PrinterCommandService>();
        FileInfoEventDataDTO? existing;

        try
        {
            CommandOutcome<FileInfoEventDataDTO>? answer = await commands.AskAsync(
                printerId, new PrusaConnect.Commands.SendFileInfo { Path = PrinterDriveNames.OnDrive(onPrinter.DriveName ?? file.FileName) },
                QueueAdvancer.CallerFor(head), cancellationToken);

            existing = answer?.Answer;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
        {
            // Could not ask. Not a block: the next pass asks again, and holding a queue on an
            // unanswered question would punish a printer that was merely busy.
            _logger.LogDebug(e, "[{PrinterId}] could not ask about the existing {FileName}",
                             printerId, file.FileName);

            return;
        }

        bool ours = PrinterDriveCopies.IsCurrent(onPrinter, digest);

        if (ours && existing?.ReadOnly == true)
        {
            _logger.LogDebug("[{PrinterId}] {FileName} is on the drive and in use; asking again on the next pass",
                             printerId, file.FileName);

            return;
        }

        // Not while it is read-only: that is a file in use, and an unfinished transfer is one - firmware
        // preallocates the partial to its full size, so the size alone would adopt it.
        if (ours && existing?.Size == file.Length && existing.ReadOnly != true)
        {
            _logger.LogInformation(
                "[{PrinterId}] {FileName} is already on the drive as {PrinterPath}, sent from these bytes; adopting it",
                printerId, file.FileName, QueueAdvancer.ForLog(existing.Path));

            onPrinter.ArrivedAt = _timeProvider.GetUtcNow();
            onPrinter.PrinterPath = existing.Path ?? file.PrinterPath;
            QueueHolds.ClearHold(onPrinter);

            return;
        }

        string refused = onPrinter.DriveName ?? file.FileName;
        string? next = await services.GetRequiredService<PrinterDriveNames>()
                                  .AfterAsync(printerId, head.PrintFile!, refused, file.FileName, cancellationToken);

        if (next is not null)
        {
            _logger.LogInformation(
                "[{PrinterId}] {DriveName} is already on the drive as another file ({PrinterBytes} bytes against {OurBytes} " +
                "here); sending {FileName} as {NextName} instead.",
                printerId, refused, existing?.Size, file.Length, file.FileName, next);

            // Everything the row said was about the old name. Nothing of ours is under the new one yet,
            // and a digest left behind would vouch for whatever turns up there.
            onPrinter.DriveName = next;
            onPrinter.Digest = null;
            onPrinter.ArrivedAt = null;
            QueueHolds.ClearHold(onPrinter);

            return;
        }

        // Every name is taken. Held rather than failed, because the entry is still wanted and a
        // person clearing the drive at the panel should see the queue resume by itself.
        //
        // Two reasons rather than one with a nullable size: "demonstrably not our file" and "cannot
        // be confirmed either way" are different things to tell somebody, and collapsing them would
        // have the page claim a certainty the printer declined to give.
        onPrinter.HoldReason = existing?.Size is not null ?
            PrintHoldReason.FileExistsDifferentSize :
            PrintHoldReason.FileExistsUnknownSize;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = existing?.Size;
        onPrinter.BlockedAt = _timeProvider.GetUtcNow();

        // English in the log on purpose, and the numbers as fields: this line is read by whoever runs
        // the deployment, while the page says the same thing to whoever is waiting for the print, in
        // their own language.
        _logger.LogWarning(
            "[{PrinterId}] {FileName} is already on the printer as {PrinterBytes} bytes against {OurBytes} here; " +
            "holding the queue.",
            printerId, file.FileName, existing?.Size, file.Length);
    }

    /// <summary>
    /// Asks the printer whether the file will fit, and holds the queue if it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked rather than remembered.</b> Free space is only on the wire in <c>INFO</c>'s storage
    /// block, and an unsolicited <c>INFO</c> arrives on connect and when <c>info_fingerprint()</c>
    /// changes - which free space is not part of. A figure kept from connect time therefore goes stale
    /// exactly as a queue fills the drive, which is the one situation it exists to catch. So the loop
    /// asks, and a held queue re-asks at <see cref="QueueAdvancer.BlockRecheckAfter"/> rather than every tick.
    /// </para>
    /// <para>
    /// <b>The queue holds behind a file that does not fit</b> (Henrik: *"Holds, like a traditional
    /// printer spooler"*). Not skipped - a shared queue whose order silently rearranges is worse than
    /// one that visibly stops - and not cancelled, because the condition is recoverable by a person
    /// deleting files, and it clears itself when they do. The failed attempt is written to print
    /// history <b>once</b>, carrying both numbers, so there is something to read rather than a queue
    /// that merely stopped.
    /// </para>
    /// <para>
    /// <b>Unknown space is treated as room.</b> A printer that reports no <c>storages</c> block tells
    /// us nothing, and refusing to print on a measurement we do not have would be worse than trying:
    /// a genuinely full drive fails the transfer loudly, where a wrong refusal is silent.
    /// </para>
    /// </remarks>
    private async Task<bool> HasRoomForAsync(IServiceProvider services,
                                             HomespoolDbContext dbContext,
                                             int printerId,
                                             QueuedPrint head,
                                             long length,
                                             PrintFileOnPrinter onPrinter,
                                             CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (onPrinter.HoldReason is not null &&
            onPrinter.BlockedAt is { } blockedAt &&
            now - blockedAt < QueueAdvancer.BlockRecheckAfter)
        {
            // Still held, and asked recently enough. Saying nothing here is deliberate: a held queue
            // that logged every tick would bury the one line that explains it.
            return false;
        }

        PrinterCommandService commands = services.GetRequiredService<PrinterCommandService>();
        long? free;

        try
        {
            CommandOutcome<InfoEventDataDTO>? answer =
                await commands.AskAsync(printerId, new PrusaConnect.Commands.SendInfo(), QueueAdvancer.CallerFor(head), cancellationToken);

            free = answer?.Answer?.Storages?
                .FirstOrDefault(storage => storage.MountPoint == "/usb")?
                .FreeSpace;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
        {
            // Could not ask. Not a block - the next pass asks again, and treating an unanswered
            // question as "no room" would hold a queue on a printer that was merely busy.
            _logger.LogDebug(e, "[{PrinterId}] could not ask about free space", printerId);

            return false;
        }

        if (free is null || free >= length)
        {
            // Only a block this check wrote is a block this check may lift. Since FILE_EXISTS started
            // holding the queue too, "there is room now" is no longer evidence that whatever is in the
            // way has gone - clearing indiscriminately would drop a file-conflict hold every minute
            // and set the transfer retrying against a refusal that has not changed.
            if (onPrinter.HoldReason == PrintHoldReason.InsufficientSpace)
            {
                _logger.LogInformation("[{PrinterId}] there is room for {FileName} now; the queue resumes",
                                       printerId, head.PrintFile!.Name);

                QueueHolds.ClearHold(onPrinter);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        bool newlyBlocked = onPrinter.HoldReason is null;

        onPrinter.HoldReason = PrintHoldReason.InsufficientSpace;
        onPrinter.HoldPrinterFreeBytes = free;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        if (newlyBlocked)
        {
            // English, and staying that way. PrintJob.Reason is a history record whose other writer is
            // HandleRefusal, passing firmware's own refusal string through verbatim - so the column
            // holds what was said at the time rather than something to re-say later. The live hold is
            // what a reader acts on, and that is HoldReason, which is localised: the two are
            // different jobs.
            string recorded = string.Create(
                CultureInfo.InvariantCulture,
                $"Not enough space on the printer: {head.PrintFile!.Name} needs {length} bytes, {free} free.");

            // Written once, on the transition. A row per tick would turn history into a log, and the
            // queue entry itself stays put - somebody still wants this printed.
            dbContext.PrintJobs.Add(new PrintJob
            {
                PrinterId = printerId,
                PrintUuid = head.PrintUuid,
                FileName = head.PrintFile.Name,
                Digest = head.PrintFile.Digest,
                QueuedByUserId = head.QueuedByUserId,
                QueuedByScope = head.QueuedByScope,
                StartedAt = now,
                EndedAt = now,
                State = PrintState.Failed,
                Reason = recorded,
            });

            _logger.LogWarning("[{PrinterId}] {Reason} The queue holds until space is freed.", printerId, recorded);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return false;
    }
}
