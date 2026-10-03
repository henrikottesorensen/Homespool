using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// The one owner of getting a file onto a printer's drive and of knowing whether it got there: sends
/// for the queue and for a person alike, and settles every transfer from the printer's own reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>One mailbox per printer, and everything about that printer's transfers goes through it.</b> A
/// send records the command its transfer started under; settling matches the transfer's end to that
/// record. Run side by side, an end could be read while its send was still waiting to record it,
/// match nothing, and be passed by the watermark for good. As messages on one loop they cannot
/// overlap, so the record is always there by the time its end is read.
/// </para>
/// <para>
/// <b>Per printer rather than one for all</b>, because a send waits for the printer's answer - up to
/// the response timeout - and one printer's slow answer must not hold up another's transfers. A
/// printer has one transfer slot, so serialising its own costs it nothing.
/// </para>
/// <para>
/// <b>Settled when the printer reports, not when the queue looks.</b> <see cref="TelemetryWriter"/>
/// says when it has saved a <c>FILE_INFO</c> or <c>TRANSFER_*</c> event, so a printer that only ever
/// takes direct sends has its transfers settled as they end. Every printer is settled once at start,
/// and the queue settles a printer before each pass; both are free, because settling is safe to
/// repeat.
/// </para>
/// <para>
/// <b>What a sender decides stays the sender's</b> - see <see cref="TransferPolicy"/> and
/// <see cref="ITransferEndPolicy"/> - but runs here, on the printer's loop and in its save.
/// </para>
/// </remarks>
public sealed class TransferService : BackgroundService, IPrinterEventObserver
{
    /// <summary>
    /// How much of a path or a reason the printer wrote is worth a log line. A drive path tops out
    /// near 260 characters; an event may be a megabyte, and its strings are the sender's to size.
    /// </summary>
    private const int MaxLoggedLength = 256;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransferService> _logger;

    /// <summary>
    /// Cancelled when the host stops: ends whatever a mailbox is doing, and refuses anything posted
    /// afterwards.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// <see cref="_stopping"/>'s token, taken once. The host may stop this service after disposing it,
    /// and a disposed source will not hand out its token any more; the token itself still answers.
    /// </summary>
    private readonly CancellationToken _stoppingToken;

    /// <summary>
    /// One mailbox per printer, made on first use. Lazy because <c>GetOrAdd</c> may run its factory
    /// twice under a race, and a mailbox starts its loop when it is made.
    /// </summary>
    private readonly ConcurrentDictionary<int, Lazy<Mailbox>> _mailboxes = new();

    // 1 once Stop has run, from StopAsync or Dispose, whichever came first.
    private int _stopped;

    public TransferService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<TransferService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _stoppingToken = _stopping.Token;
    }

    /// <summary>
    /// Sends one file to one printer: names it on the drive, clears an older copy of it, offers it, and
    /// records the attempt - with <see cref="TransferRequest.Policy"/> deciding around each step.
    /// </summary>
    /// <param name="request">What to send, where, and under whose authority.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, and the send if it has not begun.</param>
    /// <returns>How far the send got, and what the printer answered.</returns>
    /// <remarks>
    /// Waits its turn behind whatever else this printer's transfers are doing. Anything the procedure
    /// throws reaches the caller, after what it has recorded is saved: an offer the printer did not
    /// answer in time records the attempt first, since the printer may be fetching anyway.
    /// </remarks>
    public Task<TransferResult> SendAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        TaskCompletionSource<TransferResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(request.PrinterId, new SendMessage(request, completion, cancellationToken));

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Sends one of a user's files to a printer outside the queue - the API's send and the Files
    /// page's - and hands back every answer, holding and counting nothing.
    /// </summary>
    /// <param name="printer">The printer, already resolved and authorised by the caller.</param>
    /// <param name="indexed">The file's row.</param>
    /// <param name="file">The file's bytes, as the store holds them.</param>
    /// <param name="caller">The authority the send is made under.</param>
    /// <param name="cancellationToken">Cancels the send until the offer goes out, and the caller's wait.</param>
    /// <returns>What became of an older copy, and the printer's answer when the file was offered.</returns>
    /// <exception cref="PrintFileUnreadableException">The file could not be read.</exception>
    /// <remarks>
    /// Through the printer's mailbox like the queue's sends, so the attempt is recorded before its end
    /// can be read, and the end settles it whether or not the queue ever visits this printer.
    /// </remarks>
    public async Task<DirectSendResult> SendDirectAsync(Printer printer,
                                                        PrintFile indexed,
                                                        StoredFile file,
                                                        Caller caller,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(indexed);

        TransferResult result = await SendAsync(new TransferRequest(printer.Id, indexed.Id, caller, new DirectSend(file)),
                                                cancellationToken);

        // Never null for a direct send: its file is given, and one that cannot be read throws rather
        // than stopping short.
        return new DirectSendResult(result.Cleared!, result.Sent);
    }

    /// <summary>
    /// Settles this printer's transfers from whatever it has reported since they were last settled,
    /// and returns once that is done.
    /// </summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <remarks>
    /// The queue's pass begins with this, so a transfer that finished since its last pass is known
    /// before it decides anything on the assumption that it has not.
    /// </remarks>
    public Task SettleAsync(int printerId, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(printerId, new SettleMessage(completion));

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Coalesced: one settle waiting is enough however many reports arrive before it runs, since it
    /// reads everything up to the moment it starts.
    /// </remarks>
    public void Saved(int printerId, PrinterEventType eventType)
    {
        if (eventType is not (PrinterEventType.FileInfo or PrinterEventType.TransferFinished or
                              PrinterEventType.TransferAborted or PrinterEventType.TransferStopped))
        {
            return;
        }

        Nudge(printerId);
    }

    /// <summary>Settles every printer once.</summary>
    /// <remarks>
    /// An end saved just before the last stop may never have been settled, and a printer that only
    /// ever takes direct sends has nothing else to make it look. Settling twice is harmless.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<int> printerIds;

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            printerIds = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                    .Printers
                                    .Select(printer => printer.Id)
                                    .ToListAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not fatal: each printer is settled again on its next report, and the queue settles one
            // before every pass.
            _logger.LogError(e, "could not list the printers to settle their transfers at start");

            return;
        }

        foreach (int printerId in printerIds)
        {
            Nudge(printerId);
        }
    }

    /// <summary>Stops every mailbox: finishes what each is doing, and refuses the rest.</summary>
    /// <remarks>
    /// By completing the mailboxes and cancelling the work, not by abandoning the loops, so a send
    /// stopped mid-way still saves what it recorded before the cancellation reaches it.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();

        List<Task> loops = [.. _mailboxes.Values.Select(mailbox => mailbox.Value.Loop)];

        await base.StopAsync(cancellationToken);

        try
        {
            await Task.WhenAll(loops).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host's own shutdown budget ran out first; what is left dies with the process.
        }
    }

    /// <summary>Stops, if nothing has yet, and lets go of the token source.</summary>
    /// <remarks>Stops first, so nothing is left running against a disposed source.</remarks>
    public override void Dispose()
    {
        Stop();
        _stopping.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Cancels the work and completes every mailbox, once - safe from <see cref="StopAsync"/> and
    /// <see cref="Dispose"/> in either order, since the host may call both.
    /// </summary>
    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        _stopping.Cancel();

        foreach (Lazy<Mailbox> mailbox in _mailboxes.Values)
        {
            mailbox.Value.Messages.Writer.TryComplete();
        }
    }

    private static string ForLog(string? printerWritten)
    {
        return LogText.Clean(printerWritten, MaxLoggedLength);
    }

    /// <summary>Asks for a settle unless one is already waiting.</summary>
    private void Nudge(int printerId)
    {
        if (_stoppingToken.IsCancellationRequested)
        {
            return;
        }

        Mailbox mailbox = MailboxFor(printerId);

        if (mailbox.MarkSettleWaiting())
        {
            mailbox.Messages.Writer.TryWrite(new SettleMessage(Completion: null));
        }
    }

    private void Post(int printerId, Message message)
    {
        if (_stoppingToken.IsCancellationRequested || !MailboxFor(printerId).Messages.Writer.TryWrite(message))
        {
            message.Cancel(_stoppingToken);
        }
    }

    private Mailbox MailboxFor(int printerId)
    {
        return _mailboxes.GetOrAdd(printerId, id => new Lazy<Mailbox>(() => Open(id))).Value;
    }

    private Mailbox Open(int printerId)
    {
        Mailbox mailbox = new();

        // Run, not called: the loop must not begin on the thread of whoever posted first.
        mailbox.Loop = Task.Run(() => RunAsync(printerId, mailbox));

        return mailbox;
    }

    /// <summary>One printer's loop: every message in order, one at a time, until the mailbox is completed.</summary>
    /// <remarks>
    /// Nothing a message does may end the loop - each handler reports its own failure to whoever
    /// posted it - because a dead loop would leave the printer's transfers unsent and unsettled for
    /// the life of the process.
    /// </remarks>
    private async Task RunAsync(int printerId, Mailbox mailbox)
    {
        await foreach (Message message in mailbox.Messages.Reader.ReadAllAsync())
        {
            if (_stoppingToken.IsCancellationRequested)
            {
                message.Cancel(_stoppingToken);

                continue;
            }

            switch (message)
            {
                case SendMessage send:
                    await HandleSendAsync(send);
                    break;

                case SettleMessage settle:
                    await HandleSettleAsync(printerId, mailbox, settle);
                    break;

                default:
                    break;
            }
        }
    }

    private async Task HandleSendAsync(SendMessage send)
    {
        // Checked first, because the send has costs the caller is no longer there to receive: an
        // offer, a command, and this printer's mailbox for as long as the answer takes.
        if (send.CallerToken.IsCancellationRequested)
        {
            send.Completion.TrySetCanceled(send.CallerToken);

            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(send.CallerToken, _stoppingToken);

        try
        {
            send.Completion.TrySetResult(await SendCoreAsync(send.Request, linked.Token));
        }
        catch (OperationCanceledException e) when (linked.IsCancellationRequested)
        {
            send.Completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            send.Completion.TrySetException(e);
        }
    }

    private async Task HandleSettleAsync(int printerId, Mailbox mailbox, SettleMessage settle)
    {
        if (settle.Completion is null)
        {
            // Before reading, so a report saved while this one reads asks for another.
            mailbox.ClearSettleWaiting();
        }

        try
        {
            await ReconcileArrivalsAsync(printerId, mailbox, _stoppingToken);
            settle.Completion?.TrySetResult();
        }
        catch (OperationCanceledException e) when (_stoppingToken.IsCancellationRequested)
        {
            settle.Completion?.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            if (settle.Completion is null)
            {
                // Nobody is waiting on a nudge, so nobody else would hear of it. The next report, the
                // next queue pass, or the next start settles the printer again.
                _logger.LogError(e, "[{PrinterId}] could not settle transfers from the printer's reports", printerId);
            }
            else
            {
                settle.Completion.TrySetException(e);
            }
        }
    }

    /// <summary>The procedure behind <see cref="SendAsync"/>, run on the printer's mailbox.</summary>
    /// <remarks>
    /// <para>
    /// <b>The drive name is recorded before the offer</b>, so the next transfer to this printer -
    /// queued or direct, anyone's - sees it taken; and a queued attempt is stamped then too, because
    /// the printer can begin fetching the instant it accepts, and a stamp written afterwards would
    /// leave a window in which the queue saw no transfer and offered the file again.
    /// </para>
    /// <para>
    /// <b>The attempt is recorded when the printer takes it</b>, or leaves the offer unanswered, which
    /// often means it is fetching anyway - <see cref="PrinterDriveCopies.RecordTaken"/>, with the
    /// command's id that the transfer's end will name. A refused offer put nothing on the drive and
    /// records nothing.
    /// </para>
    /// <para>
    /// <b>Once the command has gone, what it did is saved whatever the caller does.</b> Its token
    /// cancels the procedure up to the offer; after it, a person leaving the page or the host
    /// stopping must not leave an accepted transfer unrecorded - its end would then match nothing.
    /// </para>
    /// <para>
    /// <b>A send that fell short clears the stamp</b> - not connected, another command in flight, the
    /// write stalled, the authority gone. <see cref="PrintFileSender"/> revoked the offer on each, so
    /// no transfer of these bytes can be running, and the abort a printer that had taken it reports
    /// names a command nothing recorded.
    /// </para>
    /// </remarks>
    private async Task<TransferResult> SendCoreAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
        TransferPolicy policy = request.Policy;
        int printerId = request.PrinterId;

        PrintFile printFile = await dbContext.PrintFiles.SingleAsync(file => file.Id == request.PrintFileId, cancellationToken);
        PrintFileOnPrinter? existing = await dbContext.PrintFilesOnPrinters
                                                      .SingleOrDefaultAsync(row => row.PrinterId == printerId &&
                                                                                   row.PrintFileId == printFile.Id,
                                                                            cancellationToken);
        TransferContext context = new(scope.ServiceProvider, dbContext, printerId, printFile, existing);

        if (await policy.FindFileAsync(context, cancellationToken) is not { } file)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(Cleared: null, Sent: null);
        }

        Printer printer = await dbContext.Printers.SingleAsync(candidate => candidate.Id == printerId, cancellationToken);
        PrintFileOnPrinter onPrinter = context.EnsureRow();

        // Chosen once and kept: the printer's reports about this file will carry it, and a retry must
        // not wander off to another name.
        onPrinter.DriveName ??= await scope.ServiceProvider.GetRequiredService<PrinterDriveNames>()
                                           .FirstAsync(printerId, printFile, file.FileName, cancellationToken);

        string digest;
        OutdatedCopyOutcome cleared;

        try
        {
            digest = await scope.ServiceProvider.GetRequiredService<PrintFileCatalog>()
                                .DigestForSendingAsync(printFile, file, cancellationToken);
            cleared = await scope.ServiceProvider.GetRequiredService<PrinterDriveCopies>()
                                 .RemoveOutdatedAsync(printerId, onPrinter, digest, request.Caller, cancellationToken);
        }
        catch (PrintFileUnreadableException e)
        {
            if (!await policy.UnreadableAsync(context, e, cancellationToken))
            {
                throw;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(Cleared: null, Sent: null);
        }

        if (!cleared.Cleared)
        {
            await policy.CopyKeptAsync(context, cleared, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(cleared, Sent: null);
        }

        if (!await policy.ReadyToSendAsync(context, file, cancellationToken))
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(cleared, Sent: null);
        }

        if (policy.StampsAttempt)
        {
            // A path left from an attempt that went stale is cleared with the stamp: it names nothing
            // this transfer has reported, and would count as a report. So is the attempt it was
            // waiting on, which this one replaces.
            onPrinter.TransferStartedAt = _timeProvider.GetUtcNow();
            onPrinter.PrinterPath = null;
            onPrinter.TransferCommandId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        FileSendResult sent;

        try
        {
            sent = await scope.ServiceProvider.GetRequiredService<PrintFileSender>()
                              .SendAsync(printer, file, PrinterDriveNames.OnDrive(onPrinter.DriveName), request.Caller,
                                         cancellationToken);
        }
        catch (CommandResponseTimedOutException e)
        {
            // Not an answer, and the stamp stays: firmware acknowledges a download late when it is busy
            // and starts fetching either way, and PrintFileSender leaves the offer standing for exactly
            // that reason. The digest is recorded for the same reason - if these bytes are arriving, an
            // older digest beside them would have them deleted and sent again for nothing - and the
            // command's id, which the transfer's end will name.
            PrinterDriveCopies.RecordTaken(onPrinter, digest, e.CommandId);
            await dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandSendTimedOutException or TeamAccessDeniedException or
                                      CredentialScopeDeniedException)
        {
            onPrinter.TransferStartedAt = null;
            await dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
        catch (PrintFileUnreadableException e)
        {
            onPrinter.TransferStartedAt = null;

            bool handled = await policy.UnreadableAsync(context, e, cancellationToken);

            await dbContext.SaveChangesAsync(CancellationToken.None);

            if (!handled)
            {
                throw;
            }

            return new TransferResult(cleared, Sent: null);
        }

        policy.Offered(context);

        if (sent.Outcome is { EventType: PrinterEventType.Rejected or PrinterEventType.Failed } refusal)
        {
            // Cleared whatever the reason, so the queue's next pass decides afresh rather than waiting
            // out the staleness timeout on a transfer that never started.
            onPrinter.TransferStartedAt = null;
            await policy.RefusedAsync(context, file, digest, refusal, cancellationToken);
        }
        else
        {
            // Taken: the drive now holds these bytes under this name, arriving.
            PrinterDriveCopies.RecordTaken(onPrinter, digest, sent.Outcome?.CommandId);
            policy.Taken(context, sent.Outcome);
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        return new TransferResult(cleared, sent);
    }

    /// <summary>
    /// Turns the printer's own reports into what is known about each file on its drive: a
    /// <c>FILE_INFO</c> names it, and the transfer's own terminal event says whether it all arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two reports, two facts.</b> Firmware sends a <c>FILE_INFO</c> a few seconds into a transfer,
    /// once the partial file is printable - <c>read_only</c>, the full size, and the 8.3 path the file
    /// keeps - and another when it completes. The first is what lets a print start while the rest
    /// downloads, which firmware supports and this queue does. It is not arrival: a transfer that
    /// fails after it leaves a partial, and treating that as present is how a queue came to print a
    /// file firmware then called a file error. So the path is taken from the first report, and
    /// <see cref="PrintFileOnPrinter.ArrivedAt"/> waits for <c>TRANSFER_FINISHED</c>.
    /// </para>
    /// <para>
    /// <b>The path is the <c>FILE_INFO</c>'s, never ours.</b> Connect transfers to the long name and
    /// starts the print with the 8.3 name the answering <c>FILE_INFO</c> reports, and deriving that name
    /// here would mean inventing a <c>~N</c> collision index against a directory we cannot see, where a
    /// wrong guess prints a different file. Matched on <c>display_name</c>, the only field carrying the
    /// name we sent.
    /// </para>
    /// <para>
    /// <b>A terminal event is matched by the command that started it.</b> It carries only
    /// <c>start_cmd_id</c>, and the row that recorded that command when the printer took the transfer
    /// is the one it ends. An event without one ended a transfer nothing here commanded - a PrusaLink
    /// upload - and is not ours to read.
    /// </para>
    /// <para>
    /// <b>Reading an event twice changes nothing</b>, which is what lets the watermark be memory - and
    /// a restart read the whole retained log again, as it does. An
    /// ending already applied names an attempt no row is waiting on, and a report from before the
    /// attempt in flight began is not taken as naming it.
    /// </para>
    /// </remarks>
    private async Task ReconcileArrivalsAsync(int printerId, Mailbox mailbox, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // What the printer has told us, which StorageOptions.TelemetryInMemory may keep in a database
        // of its own. Read by printer id, so nothing here has to join across the two.
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        long watermark = mailbox.Watermark;

        List<PrinterEvent> events = await telemetry.PrinterEvents
                                                   .AsNoTracking()
                                                   .Where(printerEvent => printerEvent.PrinterId == printerId &&
                                                                          printerEvent.Id > watermark &&
                                                                          (printerEvent.EventType == PrinterEventType.FileInfo ||
                                                                           printerEvent.EventType == PrinterEventType.TransferFinished ||
                                                                           printerEvent.EventType == PrinterEventType.TransferAborted ||
                                                                           printerEvent.EventType == PrinterEventType.TransferStopped))
                                                   .OrderBy(printerEvent => printerEvent.Id)
                                                   .ToListAsync(cancellationToken);

        // THE HIGHEST EVENT THIS READ ACTUALLY LOOKED AT, which is the only id it can claim to have
        // handled. The maximum over every event was a SECOND query issued after the one above, so
        // anything written between the two was lost outright: too new for the first query to
        // return, and, once the watermark moved past it, too old for the next pass to ask for.
        //
        // Not a narrow window. TelemetryWriter adds printer events in batches and saves them in
        // one go, and a printer mid-transfer produces a steady stream of them - TRANSFER_INFO,
        // STATE_CHANGED, JOB_INFO - so the id this claimed to have reached was routinely one
        // another connection had only just written. A batch landing between the two queries took
        // every FILE_INFO in it.
        //
        // What it costs when it happens: nothing records the file as arrived and nothing records it
        // as failed, so a queued entry waits out QueueAdvancer.TransferStaleAfter - half an hour -
        // before the file is offered again.
        long highest = events.Count > 0 ? events[^1].Id : watermark;

        bool changed = false;

        foreach (PrinterEvent printerEvent in events)
        {
            changed |= printerEvent.EventType == PrinterEventType.FileInfo ?
                await RecordReportedPathAsync(dbContext, printerId, printerEvent, cancellationToken) :
                await EndTransferAsync(scope.ServiceProvider, dbContext, printerId, printerEvent, cancellationToken);
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        mailbox.Watermark = highest;
    }

    /// <summary>
    /// Records the path a <c>FILE_INFO</c> gives one of our files that is still arriving, or has
    /// arrived without being named.
    /// </summary>
    /// <returns>Whether a row changed.</returns>
    private async Task<bool> RecordReportedPathAsync(HomespoolDbContext dbContext,
                                                     int printerId,
                                                     PrinterEvent printerEvent,
                                                     CancellationToken cancellationToken)
    {
        if (Deserialize<FileInfoEventDataDTO>(printerEvent) is not { DisplayName: { } displayName, Path: { } path })
        {
            return false;
        }

        // Matched by name in .NET and never with Single: a name is unique per user, not per printer,
        // so two members' files of one name can both have a row here, and a throw at this point
        // abandons every pass for the printer before the watermark moves. Of several, the transfer
        // in flight is the one a report describes - the printer has one transfer slot - and the
        // latest start wins over a stale one.
        //
        // Never a report received before the attempt in flight began. The log is read again from the
        // start after a restart, and the report of an earlier copy under the same name would hand a
        // transfer still arriving that copy's path - which the queue would print while it downloads.
        List<PrintFileOnPrinter> waiting = await dbContext.PrintFilesOnPrinters
                                                          .Include(candidate => candidate.PrintFile)
                                                          .Where(candidate => candidate.PrinterId == printerId &&
                                                                              (candidate.ArrivedAt == null ||
                                                                               candidate.PrinterPath == null))
                                                          .ToListAsync(cancellationToken);

        PrintFileOnPrinter? row = waiting.Where(candidate => DriveNames.Same(candidate.DriveName ?? candidate.PrintFile!.Name,
                                                                             displayName) &&
                                                             (candidate.TransferStartedAt is not { } startedAt ||
                                                              printerEvent.Timestamp >= startedAt))
                                         .OrderByDescending(candidate => candidate.TransferStartedAt is not null)
                                         .ThenByDescending(candidate => candidate.TransferStartedAt)
                                         .FirstOrDefault();

        if (row is null || row.PrinterPath == path)
        {
            return false;
        }

        row.PrinterPath = path;

        if (row.Arrived)
        {
            _logger.LogInformation("[{PrinterId}] {FileName} is on the drive as {PrinterPath}",
                                   printerId, displayName, ForLog(path));
        }
        else
        {
            _logger.LogInformation("[{PrinterId}] {FileName} is arriving on the drive as {PrinterPath}",
                                   printerId, displayName, ForLog(path));
        }

        return true;
    }

    /// <summary>
    /// Settles one of our transfers from its terminal event: arrived on <c>TRANSFER_FINISHED</c>,
    /// offered again after a wait on <c>TRANSFER_ABORTED</c> until the retry bound holds it, and held
    /// on <c>TRANSFER_STOPPED</c>, which is somebody at the printer saying no.
    /// </summary>
    /// <returns>Whether a row changed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only the attempt the row is waiting on.</b> The ending's <c>start_cmd_id</c> must be the row's
    /// <see cref="PrintFileOnPrinter.TransferCommandId"/>, and settling clears it in the same save. So
    /// an ending read twice - the watermark is memory, and a restart reads the log again - finds
    /// nothing the second time, and the end of an attempt a later one replaced finds nothing at all.
    /// </para>
    /// <para>
    /// <b>An ending that is not a finish clears the path and the digest as well as the stamp.</b>
    /// Firmware removes the partial when a transfer fails, so the name no longer holds anything of
    /// ours, and a path left behind would let the queue print it. A print already started on the
    /// partial fails on the printer, and the print's own row records that like any other failure.
    /// </para>
    /// <para>
    /// <b>Only the queue's own attempts are counted or held</b> - the ones carrying its stamp, handed to
    /// <see cref="ITransferEndPolicy"/>. A direct
    /// send's abort or stop says nothing about a print somebody queued since, and holding that print
    /// on it would replay a person's decision at the panel onto a file they never stopped.
    /// </para>
    /// </remarks>
    private async Task<bool> EndTransferAsync(IServiceProvider services,
                                              HomespoolDbContext dbContext,
                                              int printerId,
                                              PrinterEvent printerEvent,
                                              CancellationToken cancellationToken)
    {
        if (Deserialize<TransferEventDataDTO>(printerEvent)?.StartCommandId is not { } startCommandId)
        {
            return false;
        }

        // First rather than Single: ids restart at random on every connection, so two rows could in
        // principle each hold the same one, and a throw here abandons every pass for the printer.
        PrintFileOnPrinter? row = await dbContext.PrintFilesOnPrinters
                                                 .Include(candidate => candidate.PrintFile)
                                                 .Where(candidate => candidate.PrinterId == printerId &&
                                                                     candidate.TransferCommandId == startCommandId)
                                                 .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return false;
        }

        string driveName = row.DriveName ?? row.PrintFile!.Name;
        bool queued = row.TransferStartedAt is not null;

        row.TransferCommandId = null;
        row.TransferStartedAt = null;

        if (printerEvent.EventType == PrinterEventType.TransferFinished)
        {
            row.ArrivedAt = printerEvent.Timestamp;

            // A count of aborts survives acceptance, so it ends here.
            TransferRetryRules.Forget(row);

            _logger.LogInformation("[{PrinterId}] {FileName} has arrived", printerId, ForLog(driveName));

            return true;
        }

        row.PrinterPath = null;
        row.Digest = null;

        if (queued)
        {
            // The queue decides what an attempt of its own ending means: counted, held, or nothing
            // when the entry behind it has gone.
            await services.GetRequiredService<ITransferEndPolicy>()
                          .EndedAsync(new TransferContext(services, dbContext, printerId, row.PrintFile!, row),
                                      printerEvent.EventType, cancellationToken);
        }
        else
        {
            _logger.LogInformation("[{PrinterId}] the transfer of {FileName} ended with {EventType} before it finished",
                                   printerId, ForLog(driveName), printerEvent.EventType);
        }

        return true;
    }

    /// <summary>
    /// An event's payload as <typeparamref name="T"/>, or null when there is none or it does not
    /// parse.
    /// </summary>
    /// <remarks>
    /// Stored verbatim from the wire, so a payload that does not parse is a printer sending something
    /// unmodelled rather than our own corruption. Not this service's business to complain about.
    /// </remarks>
    private static T? Deserialize<T>(PrinterEvent? printerEvent)
        where T : class
    {
        if (printerEvent?.Payload is not { } payload)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A person's send: the file they chose, and every answer back to them - nothing held, counted or
    /// stamped, since nothing waits on it but them.
    /// </summary>
    private sealed class DirectSend : TransferPolicy
    {
        private readonly StoredFile _file;

        public DirectSend(StoredFile file)
        {
            _file = file;
        }

        public override Task<StoredFile?> FindFileAsync(TransferContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult<StoredFile?>(_file);
        }
    }

    /// <summary>One printer's mailbox, and the state only its loop touches.</summary>
    private sealed class Mailbox
    {
        /// <summary>Unbounded: what is posted is a person's click or the queue's one send per pass.</summary>
        public Channel<Message> Messages { get; } = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        public Task Loop { get; set; } = Task.CompletedTask;

        /// <summary>Last <c>PrinterEvent</c> id settled. Loop-only.</summary>
        public long Watermark { get; set; }

        // 1 while a nudged settle is waiting to run. Written by whoever nudges and by the loop, hence
        // Interlocked rather than loop-only like the watermark.
        private int _settleWaiting;

        /// <summary>Marks a nudged settle waiting; false when one already was.</summary>
        public bool MarkSettleWaiting()
        {
            return Interlocked.Exchange(ref _settleWaiting, 1) == 0;
        }

        /// <summary>Lets the next nudge post a settle, the waiting one having started.</summary>
        public void ClearSettleWaiting()
        {
            Volatile.Write(ref _settleWaiting, 0);
        }
    }

    private abstract record Message
    {
        /// <summary>Tells whoever posted this that it will not run.</summary>
        public abstract void Cancel(CancellationToken cancellationToken);
    }

    private sealed record SendMessage(TransferRequest Request,
                                      TaskCompletionSource<TransferResult> Completion,
                                      CancellationToken CallerToken) : Message
    {
        public override void Cancel(CancellationToken cancellationToken)
        {
            Completion.TrySetCanceled(cancellationToken);
        }
    }

    /// <param name="Completion">Whoever is waiting for the settle, or null for a nudge nobody waits on.</param>
    private sealed record SettleMessage(TaskCompletionSource? Completion) : Message
    {
        public override void Cancel(CancellationToken cancellationToken)
        {
            Completion?.TrySetCanceled(cancellationToken);
        }
    }
}

/// <summary>One send for <see cref="TransferService.SendAsync"/>.</summary>
/// <param name="PrinterId">The printer.</param>
/// <param name="PrintFileId">The file, as the catalogue indexes it.</param>
/// <param name="Caller">The authority the send - and any delete of an older copy - is made under.</param>
/// <param name="Policy">What the sender decides around the procedure.</param>
public sealed record TransferRequest(int PrinterId, long PrintFileId, Caller Caller, TransferPolicy Policy);
