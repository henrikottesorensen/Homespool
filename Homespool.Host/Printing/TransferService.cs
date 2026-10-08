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
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Firmware;
using Homespool.Host.Pages;
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
/// <b>One send per printer at a time, and a second is refused rather than queued.</b> A send can
/// hold the mailbox for the printer's whole response timeout, so letting sends pile up would let
/// one member keep a printer's mailbox busy for as long as they kept clicking - and the queue's
/// pass, which settles first, waiting behind every one of them. Refused with
/// <see cref="CommandAlreadyInFlightException"/>, what a second command to a busy printer has always
/// got, and the queue settles only when no send is waiting (<see cref="SettleUnlessSendingAsync"/>).
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
    /// <exception cref="CommandAlreadyInFlightException">Another send to this printer is waiting or under way.</exception>
    /// <remarks>
    /// Waits its turn behind a settle, which is quick, but never behind another send. Anything the
    /// procedure throws reaches the caller, after what it has recorded is saved: an offer the printer
    /// did not answer in time records the attempt first, since the printer may be fetching anyway.
    /// </remarks>
    public Task<TransferResult> SendAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_stoppingToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TransferResult>(_stoppingToken);
        }

        TaskCompletionSource<TransferResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mailbox mailbox = MailboxFor(request.PrinterId);

        // Admitted and posted under one lock, so a settle that must not wait behind a send cannot be
        // posted between the check and the post.
        lock (mailbox.Gate)
        {
            if (mailbox.SendPending)
            {
                return Task.FromException<TransferResult>(new CommandAlreadyInFlightException(request.PrinterId));
            }

            if (!mailbox.Messages.Writer.TryWrite(new SendMessage(request, completion, cancellationToken)))
            {
                // Completed: the service stopped since the check above.
                return Task.FromCanceled<TransferResult>(_stoppingToken);
            }

            mailbox.SendPending = true;
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Settles this printer's transfers and returns true once that is done - or returns false at once,
    /// settling nothing, when a send to the printer is waiting or under way.
    /// </summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <remarks>
    /// <b>The queue's pass, which must never wait on a send</b>: the passes run one printer after
    /// another, so a send waiting out a silent printer's response timeout would hold up every queue.
    /// Deciding on what is already settled costs nothing the queue cannot absorb - the snapshot reads a
    /// transfer in flight from the stamp and the offer, which are there before any report is - and the
    /// printer's reports are settled anyway as they are saved.
    /// </remarks>
    public async Task<bool> SettleUnlessSendingAsync(int printerId, CancellationToken cancellationToken)
    {
        _stoppingToken.ThrowIfCancellationRequested();

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mailbox mailbox = MailboxFor(printerId);

        lock (mailbox.Gate)
        {
            if (mailbox.SendPending)
            {
                return false;
            }

            if (!mailbox.Messages.Writer.TryWrite(new SettleMessage(completion)))
            {
                completion.TrySetCanceled(_stoppingToken);
            }
        }

        await completion.Task.WaitAsync(cancellationToken);

        return true;
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
    /// <exception cref="PrintFileTooLargeException">The file is too large for a printer to be sent.</exception>
    /// <exception cref="CredentialScopeDeniedException">The credential's scope does not allow printing on it.</exception>
    /// <exception cref="TeamAccessDeniedException">The printer's team does not allow this caller to print.</exception>
    /// <exception cref="PrinterInstallingFirmwareException">Firmware is being installed on the printer, from another file.</exception>
    /// <remarks>
    /// <para>
    /// Through the printer's mailbox like the queue's sends, so the attempt is recorded before its end
    /// can be read, and the end settles it whether or not the queue ever visits this printer.
    /// </para>
    /// <para>
    /// <b><see cref="Capability.Print"/> is asked before anything else</b>, because the send writes
    /// before the printer is asked anything: the file's name on the drive is chosen and saved ahead
    /// of the offer, so a refusal at the offer would leave that name reserved for a file this caller
    /// could never send.
    /// </para>
    /// </remarks>
    public async Task<DirectSendResult> SendDirectAsync(Printer printer,
                                                        HSFile indexed,
                                                        StoredFile file,
                                                        Caller caller,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(indexed);

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrinterAccessService>()
                       .RequireAsync(printer.Id, caller, Capability.Print, cancellationToken);

            // The install sends its own image this way, and nothing else goes while it runs: the flash
            // restarts the printer under any transfer still going.
            if (scope.ServiceProvider.GetRequiredService<IFirmwareInstallations>().ImageBeingInstalled(printer.Id) is long image &&
                image != indexed.Id)
            {
                throw new PrinterInstallingFirmwareException(printer.Id, PrinterDisplayName.For(printer));
            }
        }

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
    /// Waits behind a send to the printer, if one is under way. The queue's pass must not, and uses
    /// <see cref="SettleUnlessSendingAsync"/>.
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
            try
            {
                if (_stoppingToken.IsCancellationRequested)
                {
                    ReleaseSend(mailbox, message);
                    message.Cancel(_stoppingToken);

                    continue;
                }

                switch (message)
                {
                    case SendMessage send:
                        await HandleSendAsync(mailbox, send);
                        break;

                    case SettleMessage settle:
                        await HandleSettleAsync(printerId, mailbox, settle);
                        break;

                    default:
                        break;
                }
            }
            finally
            {
                // The send's own handling has released it already, before telling its caller; this is
                // for a handler that threw, which must not leave the printer refusing sends for good.
                ReleaseSend(mailbox, message);
            }
        }
    }

    /// <summary>
    /// Lets the next send be admitted. Always before a send's caller is told how it ended: a caller
    /// woken by the result goes straight on to its next pass, and a pass that found the printer still
    /// sending would skip its settle and decide on stale state.
    /// </summary>
    /// <remarks>
    /// <b>Once for each send, and only by that send.</b> It is reached twice - by the handler, ahead of
    /// telling the caller, and by the loop's net for a handler that threw - and the flag is one for the
    /// whole mailbox. A send admitted between the two had claimed it, and the second release let go of
    /// that send's claim: a third was then admitted behind it instead of refused.
    /// </remarks>
    private static void ReleaseSend(Mailbox mailbox, Message message)
    {
        if (message is not SendMessage send)
        {
            return;
        }

        lock (mailbox.Gate)
        {
            if (send.Released)
            {
                return;
            }

            send.Released = true;
            mailbox.SendPending = false;
        }
    }

    private async Task HandleSendAsync(Mailbox mailbox, SendMessage send)
    {
        // Checked first, because the send has costs the caller is no longer there to receive: an
        // offer, a command, and this printer's mailbox for as long as the answer takes.
        if (send.CallerToken.IsCancellationRequested)
        {
            ReleaseSend(mailbox, send);
            send.Completion.TrySetCanceled(send.CallerToken);

            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(send.CallerToken, _stoppingToken);

        // Awaited to an outcome first and told to the caller after the release, never from inside the try.
        Action tell;

        try
        {
            TransferResult result = await SendCoreAsync(send.Request, linked.Token);

            tell = () => send.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException e) when (linked.IsCancellationRequested)
        {
            tell = () => send.Completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            tell = () => send.Completion.TrySetException(e);
        }

        ReleaseSend(mailbox, send);
        tell();
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
    /// A caller cancelled while the printer already had the command is told so by
    /// <see cref="CommandCancelledAfterDeliveryException"/>, and the attempt is recorded as taken.
    /// </para>
    /// <para>
    /// <b>A send that fell short clears the stamp</b> - not connected, another command in flight, the
    /// write stalled, the authority gone. <see cref="FileSender"/> revoked the offer on each, so
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

        HSFile printFile = await dbContext.Files.SingleAsync(file => file.Id == request.FileId, cancellationToken);
        FileOnPrinter? existing = await dbContext.FilesOnPrinters
                                                 .SingleOrDefaultAsync(row => row.PrinterId == printerId &&
                                                                              row.FileId == printFile.Id,
                                                                       cancellationToken);
        TransferContext context = new(scope.ServiceProvider, dbContext, printerId, printFile, existing);

        StoredFile? file = await policy.FindFileAsync(context, cancellationToken);

        if (file is null)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(Cleared: null, Sent: null);
        }

        // Before the drive name is chosen and before an older copy is deleted, so that a file that can
        // never be sent costs the printer nothing - least of all the copy it already has.
        if (file.Length >= FileSender.SizeLimit)
        {
            PrintFileTooLargeException tooLarge = new();
            context.EnsureRow();

            if (!await policy.TooLargeAsync(context, tooLarge, cancellationToken))
            {
                throw tooLarge;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return new TransferResult(Cleared: null, Sent: null);
        }

        Printer printer = await dbContext.Printers.SingleAsync(candidate => candidate.Id == printerId, cancellationToken);
        FileOnPrinter onPrinter = context.EnsureRow();

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
            sent = await scope.ServiceProvider.GetRequiredService<FileSender>()
                              .SendAsync(printer, file, PrinterDriveNames.OnDrive(onPrinter.DriveName), request.Caller,
                                         cancellationToken);
        }
        catch (Exception e) when (e is CommandResponseTimedOutException or CommandCancelledAfterDeliveryException)
        {
            // Not an answer, and the stamp stays: firmware acknowledges a download late when it is busy
            // and starts fetching either way, and FileSender leaves the offer standing for exactly
            // that reason. The digest is recorded for the same reason - if these bytes are arriving, an
            // older digest beside them would have them deleted and sent again for nothing - and the
            // command's id, which the transfer's end will name. A caller that stopped waiting once the
            // printer had the command leaves the same transfer running, and is recorded the same way.
            uint? commandId = e switch
            {
                CommandResponseTimedOutException timedOut => timedOut.CommandId,
                CommandCancelledAfterDeliveryException cancelled => cancelled.CommandId,
                _ => null,
            };

            PrinterDriveCopies.RecordTaken(onPrinter, digest, commandId);
            ReplaceQueuedAttempt(policy, onPrinter);
            await dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandSendTimedOutException or TeamAccessDeniedException or
                                      CredentialScopeDeniedException)
        {
            ClearOwnStamp(policy, onPrinter);
            await dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
        catch (PrintFileUnreadableException e)
        {
            ClearOwnStamp(policy, onPrinter);

            bool handled = await policy.UnreadableAsync(context, e, cancellationToken);

            await dbContext.SaveChangesAsync(CancellationToken.None);

            if (!handled)
            {
                throw;
            }

            return new TransferResult(cleared, Sent: null);
        }
        catch (PrintFileTooLargeException e)
        {
            // The file grew past the ceiling between being found and being offered. The sender
            // revoked the offer, so nothing is running; the stamp goes as it does for a send that
            // fell short.
            ClearOwnStamp(policy, onPrinter);

            bool handled = await policy.TooLargeAsync(context, e, cancellationToken);

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
            ClearOwnStamp(policy, onPrinter);
            await policy.RefusedAsync(context, file, digest, refusal, cancellationToken);
        }
        else
        {
            // Taken: the drive now holds these bytes under this name, arriving.
            PrinterDriveCopies.RecordTaken(onPrinter, digest, sent.Outcome?.CommandId);
            ReplaceQueuedAttempt(policy, onPrinter);
            policy.Taken(context, sent.Outcome);
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        return new TransferResult(cleared, sent);
    }

    /// <summary>
    /// Clears the stamp when this attempt set it. Not saved.
    /// </summary>
    /// <remarks>
    /// <b>A direct send that falls short leaves the queue's attempt alone.</b> The stamp is the queue's
    /// record of a transfer of its own still running, and a person's send of the same file being
    /// refused - most often because that very transfer has the printer's slot - says nothing about it.
    /// </remarks>
    private static void ClearOwnStamp(TransferPolicy policy, FileOnPrinter onPrinter)
    {
        if (policy.StampsAttempt)
        {
            onPrinter.TransferStartedAt = null;
        }
    }

    /// <summary>
    /// Ends the queue's claim on a row whose attempt a direct send has just replaced. Not saved.
    /// </summary>
    /// <remarks>
    /// The printer has one transfer slot, so a download it has taken (or may have taken, unanswered)
    /// is the one running: whatever the queue started is not. The row now awaits the direct send's
    /// command, and that send's end must not be counted or held against the queue's entry.
    /// </remarks>
    private static void ReplaceQueuedAttempt(TransferPolicy policy, FileOnPrinter onPrinter)
    {
        if (!policy.StampsAttempt)
        {
            onPrinter.TransferStartedAt = null;
        }
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
    /// <see cref="FileOnPrinter.ArrivedAt"/> waits for <c>TRANSFER_FINISHED</c>.
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
        List<FileOnPrinter> waiting = await dbContext.FilesOnPrinters
                                                     .Include(candidate => candidate.File)
                                                     .Where(candidate => candidate.PrinterId == printerId &&
                                                                         (candidate.ArrivedAt == null ||
                                                                          candidate.PrinterPath == null))
                                                     .ToListAsync(cancellationToken);

        FileOnPrinter? row = waiting.Where(candidate => DriveNames.Same(candidate.DriveName ?? candidate.File!.Name,
                                                                        displayName) &&
                                                        (candidate.TransferStartedAt is not DateTimeOffset startedAt ||
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
    /// <see cref="FileOnPrinter.TransferCommandId"/>, and settling clears it in the same save. So
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
        if (Deserialize<TransferEventDataDTO>(printerEvent)?.StartCommandId is not uint startCommandId)
        {
            return false;
        }

        // First rather than Single: ids restart at random on every connection, so two rows could in
        // principle each hold the same one, and a throw here abandons every pass for the printer.
        FileOnPrinter? row = await dbContext.FilesOnPrinters
                                            .Include(candidate => candidate.File)
                                            .Where(candidate => candidate.PrinterId == printerId &&
                                                                candidate.TransferCommandId == startCommandId)
                                            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return false;
        }

        string driveName = row.DriveName ?? row.File!.Name;
        bool queued = row.TransferStartedAt is not null;

        row.TransferCommandId = null;
        row.TransferStartedAt = null;

        if (printerEvent.EventType == PrinterEventType.TransferFinished)
        {
            row.ArrivedAt = printerEvent.Timestamp;

            // A count of aborts survives acceptance, so it ends here; and the asks after a name were
            // about an earlier arrival, if any.
            TransferRetryRules.Forget(row);
            PrinterDriveCopies.ForgetPathAsks(row);

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
                          .EndedAsync(new TransferContext(services, dbContext, printerId, row.File!, row),
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
        string? payload = printerEvent?.Payload;

        if (payload is null)
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

        /// <summary>Taken to admit a send, to post a settle that must not wait behind one, and to end a send.</summary>
        public Lock Gate { get; } = new();

        /// <summary>Whether a send is waiting or under way. Read and written under <see cref="Gate"/>.</summary>
        public bool SendPending { get; set; }

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
        /// <summary>Whether this send has let go of the mailbox's one-send flag. Read and written under <see cref="Mailbox.Gate"/>.</summary>
        public bool Released { get; set; }

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
/// <param name="FileId">The file, as the catalogue indexes it.</param>
/// <param name="Caller">The authority the send - and any delete of an older copy - is made under.</param>
/// <param name="Policy">What the sender decides around the procedure.</param>
public sealed record TransferRequest(int PrinterId, long FileId, Caller Caller, TransferPolicy Policy);
