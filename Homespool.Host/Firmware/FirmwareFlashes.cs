using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Pages;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Firmware;

/// <summary>
/// Installs a stored firmware image on a printer: sends it, waits for it to arrive, flashes it, and
/// waits for the printer to come back on its version.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held in memory, one flash per printer.</b> A flash takes minutes and the page shows how far it
/// has got; a restart part-way through loses only that display, because the version the printer
/// reports when it reconnects is the truth about whether it worked.
/// </para>
/// <para>
/// <b>Refused up front, and checked again before the flash.</b> The printer must be connected, idle
/// or finished by the rule a heater or an unload uses, and have nothing queued. The printer's state is
/// checked again immediately before <see cref="FlashFirmware"/> is sent, because the image takes
/// minutes to arrive and somebody at the printer may have started a print meanwhile - from telemetry,
/// and then from the printer itself (<see cref="SendStateInfo"/>), since telemetry can be seconds old
/// and firmware flashes whatever it is doing. A print started in the round trip before the flash is
/// still reset; that window is the printer's to close, not Homespool's.
/// </para>
/// <para>
/// <b>Nothing in Homespool gives the printer work while it runs</b>
/// (<see cref="IFirmwareInstallations"/>): the queue waits, so a print queued meanwhile starts once
/// the printer is back rather than in the moments before the flash resets it. The queue is why the
/// second check does not ask about queued prints.
/// </para>
/// <para>
/// <b>Nor a transfer: the printer is held before it is checked.</b> The install takes the printer's
/// send mailbox (<see cref="TransferService.TryHoldForInstall"/>) only when no send is waiting or
/// under way, and from then on a send of any other file is refused. A transfer a send started
/// earlier, which the printer may still be pulling after the send has returned, shows as a standing
/// offer (<see cref="ITransferOffers.HasStandingOffer"/>) - the only way a printer fetches bytes -
/// and both checks refuse while one stands. Held first and checked second, a send cannot slip in
/// between.
/// </para>
/// <para>
/// <b>Through the transfer path every file takes</b>, as the person's own send: the mailbox, the
/// record of what is on the drive, and the arrival and short path the printer reports. So the person
/// needs <see cref="Capability.Print"/> on the printer as well as <see cref="Capability.ManagePrinter"/>,
/// asked when the flash starts - an image already on the drive is not sent again, and would otherwise
/// be flashed without it.
/// </para>
/// <para>
/// <b>One name on the drive, <see cref="FlashFirmware.DriveName"/></b> - see that class for why. Any
/// earlier image left on this printer is deleted first, so the name is free, and this one is deleted
/// once the printer is back on its version.
/// </para>
/// </remarks>
public sealed class FirmwareFlashes : IFirmwareInstallations
{
    private readonly ConcurrentDictionary<int, FirmwareFlashStatus> _flashes = new();
    private readonly Lock _gate = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrinterConnectionRegistry _registry;
    private readonly TransferService _transfers;
    private readonly FirmwareFlashTimings _timings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FirmwareFlashes> _logger;
    private readonly CancellationToken _stopping;

    public FirmwareFlashes(IServiceScopeFactory scopeFactory,
                           PrinterConnectionRegistry registry,
                           TransferService transfers,
                           FirmwareFlashTimings timings,
                           TimeProvider timeProvider,
                           IHostApplicationLifetime lifetime,
                           ILogger<FirmwareFlashes> logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        _scopeFactory = scopeFactory;
        _registry = registry;
        _transfers = transfers;
        _timings = timings;
        _timeProvider = timeProvider;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>The printer's latest flash, running or finished, or null when there has been none since start.</summary>
    public FirmwareFlashStatus? For(int printerId)
    {
        return _flashes.TryGetValue(printerId, out FirmwareFlashStatus? status) ? status : null;
    }

    /// <inheritdoc />
    public bool IsInstalling(int printerId)
    {
        return For(printerId)?.IsRunning == true;
    }

    /// <inheritdoc />
    public long? ImageBeingInstalled(int printerId)
    {
        return For(printerId) is { IsRunning: true } status ? status.ImageId : null;
    }

    /// <inheritdoc />
    public bool IsInstallingImage(long fileId)
    {
        return _flashes.Values.Any(status => status.IsRunning && status.ImageId == fileId);
    }

    /// <summary>
    /// Starts installing the stored image <paramref name="digest"/> on <paramref name="printerId"/>,
    /// and returns once it is under way.
    /// </summary>
    /// <exception cref="FirmwareFlashRefusedException">The printer or the image is not ready for it.</exception>
    /// <exception cref="TeamAccessDeniedException">The caller may not manage this printer.</exception>
    public async Task<FirmwareFlashStatus> StartAsync(Caller caller,
                                                      int printerId,
                                                      string digest,
                                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        string printerName;
        FirmwareToFlash? image;

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            image = await scope.ServiceProvider.GetRequiredService<FirmwareImages>()
                               .FindForFlashingAsync(caller, printerId, digest, cancellationToken);

            // Here rather than left to the send, which an image already on the drive skips.
            await scope.ServiceProvider.GetRequiredService<PrinterAccessService>()
                       .RequireAsync(printerId, caller, Capability.Print, cancellationToken);

            Printer printer = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                         .Printers
                                         .AsNoTracking()
                                         .SingleAsync(candidate => candidate.Id == printerId, cancellationToken);

            printerName = PrinterDisplayName.For(printer);

            if (image is null)
            {
                throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.NoSuchImage);
            }
        }

        FirmwareFlashStatus status = new(printerId, image.Row.Id, image.Row.Name, image.Header.Version, FirmwareFlashStage.Sending,
                                         _timeProvider.GetUtcNow());
        FirmwareFlashStatus? previous;

        // Claimed before the printer is checked, so nothing can start on it between the check and the
        // claim: the mailbox refuses other sends and the queue waits from here.
        lock (_gate)
        {
            previous = For(printerId);

            if (previous?.IsRunning == true)
            {
                throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.AlreadyFlashing);
            }

            if (!_transfers.TryHoldForInstall(printerId, image.Row.Id))
            {
                throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.Busy);
            }

            _flashes[printerId] = status;
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            await RequireReadyAsync(scope.ServiceProvider, printerId, printerName, queueMatters: true, cancellationToken);
        }
        catch
        {
            // Refused: as if never started - the hold let go, and the page showing what it showed before.
            lock (_gate)
            {
                if (previous is null)
                {
                    _flashes.TryRemove(printerId, out _);
                }
                else
                {
                    _flashes[printerId] = previous;
                }
            }

            _transfers.ReleaseInstall(printerId);

            throw;
        }

        _logger.LogInformation("[{PrinterId}] installing firmware {Version} from {FileName}, for user {UserId}",
                               printerId, image.Header.Version, image.Row.Name, caller.UserId);

        _ = Task.Run(() => RunAsync(caller, printerId, printerName, image), CancellationToken.None);

        return status;
    }

    /// <summary>
    /// The printer is connected, idle or finished, pulling no file, and - when
    /// <paramref name="queueMatters"/> - has nothing queued; or why not.
    /// </summary>
    private static async Task RequireReadyAsync(IServiceProvider services,
                                                int printerId,
                                                string printerName,
                                                bool queueMatters,
                                                CancellationToken cancellationToken)
    {
        QueueSnapshot snapshot = await services.GetRequiredService<QueueSnapshotReader>()
                                               .ReadAsync(printerId, cancellationToken);

        if (!snapshot.Connected)
        {
            throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.NotConnected);
        }

        // The snapshot's transfer is the queue head's alone; any other file still being pulled - a
        // direct send that returned before the install began - stands as an offer until it ends. The
        // install's own image is no exception: its offer is gone before it reads as arrived.
        bool pulling = services.GetRequiredService<ITransferOffers>().HasStandingOffer(printerId);

        if (!PhysicalChangeRules.IsAllowed(snapshot.Status) || snapshot.PrintInFlight || snapshot.TransferInFlight || pulling)
        {
            throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.Busy);
        }

        if (queueMatters && snapshot.Head is not null)
        {
            throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.Queued);
        }
    }

    private async Task RunAsync(Caller caller, int printerId, string printerName, FirmwareToFlash image)
    {
        CancellationToken cancellationToken = _stopping;
        string version = image.Header.Version;

        try
        {
            if (!await ClearEarlierImagesAsync(caller, printerId, printerName, image.Row.Id, cancellationToken))
            {
                return;
            }

            if (!await IsOnDriveAsync(printerId, image.Row, cancellationToken) &&
                (!await NameIsFreeAsync(caller, printerId, printerName, cancellationToken) ||
                 !await SendAsync(caller, printerId, printerName, image, cancellationToken)))
            {
                return;
            }

            Advance(printerId, FirmwareFlashStage.Arriving);

            string? path = await WaitForArrivalAsync(caller, printerId, printerName, image.Row.Id, cancellationToken);

            if (path is null)
            {
                return;
            }

            if (!string.Equals(path, FlashFirmware.DrivePath, StringComparison.OrdinalIgnoreCase))
            {
                // The flash names one path and nothing else; an image the printer filed elsewhere is not
                // one this can flash.
                Fail(printerId, new MessageKey("Firmware_FailedWrongPath", [printerName, path]));

                return;
            }

            DateTimeOffset flashedAt;

            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                // Not the queue: it has waited since the start, and anything queued since waits too.
                await RequireReadyAsync(scope.ServiceProvider, printerId, printerName, queueMatters: false, cancellationToken);

                PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

                // And the printer itself, last. The check above reads telemetry, which may be seconds
                // old, and firmware runs the flash whatever it is doing - so a print started at the
                // panel in those seconds would be reset under it. Its own answer is a round trip old:
                // that narrows the window, and nothing on this side can close it.
                CommandOutcome? state = await commands.SendCommandAsync(printerId, new SendStateInfo(), caller, cancellationToken);

                if (state?.PrinterStatus is not PrinterStatus status || !PhysicalChangeRules.IsAllowed(status))
                {
                    throw new FirmwareFlashRefusedException(printerName, FirmwareFlashRefusal.Busy);
                }

                flashedAt = _timeProvider.GetUtcNow();

                await commands.SendCommandAsync(printerId, new FlashFirmware(), caller, cancellationToken);
            }

            Advance(printerId, FirmwareFlashStage.Restarting);
            _logger.LogInformation("[{PrinterId}] flash of firmware {Version} sent; waiting for the printer to come back",
                                   printerId, version);

            string? reported = await WaitForVersionAsync(printerId, version, flashedAt, cancellationToken);

            if (!string.Equals(reported, version, StringComparison.Ordinal))
            {
                Fail(printerId, new MessageKey("Firmware_FailedNotBack",
                                               [printerName, version, (int)_timings.RestartTimeout.TotalMinutes, reported ?? "-"]));

                return;
            }

            // Installed whatever this answers. A delete that does not go through keeps the image's
            // record, so the next flash deletes it first rather than finding the name taken.
            Advance(printerId, FirmwareFlashStage.CleaningUp);
            await RemoveFromDriveAsync(caller, printerId, image.Row.Id, FlashFirmware.DrivePath, cancellationToken);

            Advance(printerId, FirmwareFlashStage.Done);
            _logger.LogInformation("[{PrinterId}] now runs firmware {Version}", printerId, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fail(printerId, new MessageKey("Firmware_FailedStopped", [printerName]));
        }
        catch (Exception e) when (e is ILocalisableError)
        {
            ILocalisableError error = (ILocalisableError)e;

            _logger.LogWarning(e, "[{PrinterId}] firmware {Version} not installed", printerId, version);
            Fail(printerId, new MessageKey(error.ResourceKey, error.ResourceArguments));
        }
        catch (Exception e)
        {
            // The flash runs unattended, so nothing may escape it: an exception here would end the task
            // silently and leave the page saying the flash is still under way.
            _logger.LogError(e, "[{PrinterId}] firmware {Version} not installed", printerId, version);
            Fail(printerId, new MessageKey("Firmware_FailedUnexpectedly", [printerName]));
        }
        finally
        {
            // Done or failed, the printer takes sends again.
            _transfers.ReleaseInstall(printerId);
        }
    }

    /// <summary>
    /// Deletes every other firmware image Homespool left on this printer, so the one name on the drive
    /// is free - or fails the flash, and answers false, when the printer will not delete one.
    /// </summary>
    /// <remarks>
    /// Stopping is the only useful answer: the printer refuses a download onto a name that is taken,
    /// so sending anyway would fail on the same file with a less helpful reason.
    /// </remarks>
    private async Task<bool> ClearEarlierImagesAsync(Caller caller,
                                                     int printerId,
                                                     string printerName,
                                                     long fileId,
                                                     CancellationToken cancellationToken)
    {
        List<FileOnPrinter> earlier;

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            earlier = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                 .FilesOnPrinters
                                 .AsNoTracking()
                                 .Where(row => row.PrinterId == printerId &&
                                               row.FileId != fileId &&
                                               row.File!.Type == FileType.PrusaFirmware)
                                 .ToListAsync(cancellationToken);
        }

        foreach (FileOnPrinter row in earlier)
        {
            string path = row.PrinterPath ?? PrinterDriveNames.OnDrive(row.DriveName ?? FlashFirmware.DriveName);

            if (!await RemoveFromDriveAsync(caller, printerId, row.FileId, path, cancellationToken))
            {
                Fail(printerId, new MessageKey("Firmware_FailedEarlierImageKept", [printerName, path]));

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether nothing is at <see cref="FlashFirmware.DrivePath"/> - or fails the flash, and answers
    /// false, when something is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked of the printer, because Homespool's record cannot answer it.</b> Earlier images it
    /// recorded are already gone by now; a file still there is one it never recorded - put on the drive
    /// by hand, or left from before its records were rebuilt - and the printer would refuse the download
    /// onto it. It is not deleted: Homespool cannot vouch for what it is, so somebody at the printer
    /// decides.
    /// </para>
    /// <para>
    /// <b>Anything but a description of the file counts as free</b> - a refusal is firmware's answer for
    /// a missing file, and when the printer cannot be asked at all the download that follows meets the
    /// same refusal and reports it.
    /// </para>
    /// </remarks>
    private async Task<bool> NameIsFreeAsync(Caller caller, int printerId, string printerName, CancellationToken cancellationToken)
    {
        CommandOutcome<FileInfoEventDataDTO>? answer;

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            answer = await scope.ServiceProvider.GetRequiredService<PrinterCommandService>()
                                .AskAsync(printerId, new SendFileInfo { Path = FlashFirmware.DrivePath }, caller, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogInformation(e, "[{PrinterId}] could not ask whether {Path} is taken", printerId, FlashFirmware.DrivePath);

            return true;
        }

        if (answer?.EventType != PrinterEventType.FileInfo)
        {
            return true;
        }

        Fail(printerId, new MessageKey("Firmware_FailedUnknownImage", [printerName, FlashFirmware.DriveName]));

        return false;
    }

    /// <summary>Whether this image already sits whole on the drive, from an earlier attempt that got that far.</summary>
    private async Task<bool> IsOnDriveAsync(int printerId, HSFile image, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        FileOnPrinter? row = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                        .FilesOnPrinters
                                        .AsNoTracking()
                                        .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId &&
                                                                           candidate.FileId == image.Id,
                                                              cancellationToken);

        return row is { Arrived: true, PrinterPath: not null } && row.Digest == image.Digest;
    }

    /// <summary>Offers the image to the printer; false, with the flash failed, when the printer will not take it.</summary>
    private async Task<bool> SendAsync(Caller caller,
                                       int printerId,
                                       string printerName,
                                       FirmwareToFlash image,
                                       CancellationToken cancellationToken)
    {
        DirectSendResult result;

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            Printer printer = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                         .Printers
                                         .AsNoTracking()
                                         .SingleAsync(candidate => candidate.Id == printerId, cancellationToken);

            result = await scope.ServiceProvider.GetRequiredService<TransferService>()
                                .SendDirectAsync(printer, image.Row, image.File, caller, cancellationToken);
        }

        if (result.Sent is null)
        {
            Fail(printerId, new MessageKey("Firmware_FailedOlderCopy", [printerName, result.Cleared.Reason ?? string.Empty]));

            return false;
        }

        if (result.Sent.Outcome is { EventType: PrinterEventType.Rejected or PrinterEventType.Failed } refusal)
        {
            Fail(printerId, new MessageKey("Firmware_FailedRefused", [printerName, refusal.Reason ?? string.Empty]));

            return false;
        }

        return true;
    }

    /// <summary>
    /// The image's short path once it has arrived whole - or null, with the flash failed, when the
    /// transfer ended short or ran out of time.
    /// </summary>
    /// <remarks>
    /// Read from the record the transfer path keeps, which settles the printer's reports as they are
    /// saved. An image that arrived without its path being reported has the printer asked once; the
    /// answer is a report like any other, and is settled the same way.
    /// </remarks>
    private async Task<string?> WaitForArrivalAsync(Caller caller,
                                                    int printerId,
                                                    string printerName,
                                                    long fileId,
                                                    CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _timings.ArrivalTimeout;
        DateTimeOffset? arrivedSeen = null;
        bool asked = false;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            FileOnPrinter? row;

            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                row = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                 .FilesOnPrinters
                                 .AsNoTracking()
                                 .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId && candidate.FileId == fileId,
                                                       cancellationToken);

                if (row is { Arrived: true, PrinterPath: string path })
                {
                    return path;
                }

                // An ending that is not a finish clears the digest along with the attempt.
                if (row is null || row is { Arrived: false, Digest: null, TransferCommandId: null })
                {
                    Fail(printerId, new MessageKey("Firmware_FailedTransferEnded", [printerName]));

                    return null;
                }

                if (row.Arrived)
                {
                    arrivedSeen ??= _timeProvider.GetUtcNow();

                    if (!asked && _timeProvider.GetUtcNow() - arrivedSeen >= _timings.PathGrace)
                    {
                        asked = true;
                        await AskForPathAsync(scope.ServiceProvider, caller, printerId, cancellationToken);
                    }
                }
            }

            await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken);
        }

        Fail(printerId, new MessageKey("Firmware_FailedArrivalTimeout", [printerName, (int)_timings.ArrivalTimeout.TotalMinutes]));

        return null;
    }

    private async Task AskForPathAsync(IServiceProvider services, Caller caller, int printerId, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<PrinterCommandService>()
                          .AskAsync(printerId, new SendFileInfo { Path = FlashFirmware.DrivePath }, caller, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Asked once and only as a nudge: the arrival still stands, and the wait runs out on its own.
            _logger.LogInformation(e, "[{PrinterId}] could not ask for the firmware image's path", printerId);
        }
    }

    /// <summary>
    /// The version the printer reports once it has reconnected after <paramref name="flashedAt"/> on
    /// <paramref name="version"/> - or the last version it reported when the wait runs out.
    /// </summary>
    private async Task<string?> WaitForVersionAsync(int printerId,
                                                    string version,
                                                    DateTimeOffset flashedAt,
                                                    CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _timings.RestartTimeout;
        string? reported = null;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                reported = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                      .Printers
                                      .AsNoTracking()
                                      .Where(printer => printer.Id == printerId)
                                      .Select(printer => printer.Firmware)
                                      .SingleOrDefaultAsync(cancellationToken);
            }

            // Only a connection made since the flash counts: the one it was sent on reports the old
            // version until it drops, and a version read from it would be no evidence of anything.
            if (_registry.ConnectedSince(printerId) is DateTimeOffset since && since > flashedAt &&
                string.Equals(reported, version, StringComparison.Ordinal))
            {
                return reported;
            }

            await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken);
        }

        return reported;
    }

    /// <summary>
    /// Deletes an image Homespool put on the printer's drive and forgets it, answering whether it is
    /// gone. A printer that answers "not found" has already lost it, which is the same end.
    /// </summary>
    /// <remarks>
    /// <b>The record goes only with the file.</b> A delete that is refused - the file in use - or that
    /// never reaches the printer leaves the image recorded, which is what lets the next flash find it
    /// and try again; forgotten, it would be a file nothing knows about holding the one name a flash
    /// can use.
    /// </remarks>
    private async Task<bool> RemoveFromDriveAsync(Caller caller,
                                                  int printerId,
                                                  long fileId,
                                                  string path,
                                                  CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        CommandOutcome? outcome;

        try
        {
            outcome = await scope.ServiceProvider.GetRequiredService<PrinterCommandService>()
                                 .SendCommandAsync(printerId, new DeleteFile { Path = path }, caller, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogInformation(e, "[{PrinterId}] could not delete firmware image {Path} from the drive", printerId, path);

            return false;
        }

        if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed &&
            outcome.Reason != PrinterDriveCopies.NotFound)
        {
            _logger.LogInformation("[{PrinterId}] kept firmware image {Path}: {Reason}", printerId, path,
                                   LogText.Clean(outcome.Reason));

            return false;
        }

        await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                   .FilesOnPrinters
                   .Where(row => row.PrinterId == printerId && row.FileId == fileId)
                   .ExecuteDeleteAsync(cancellationToken);

        return true;
    }

    private void Advance(int printerId, FirmwareFlashStage stage)
    {
        _flashes.AddOrUpdate(printerId,
                             _ => throw new InvalidOperationException($"No flash of printer {printerId} to advance."),
                             (_, status) => status with { Stage = stage });
    }

    private void Fail(int printerId, MessageKey problem)
    {
        _logger.LogWarning("[{PrinterId}] firmware flash stopped: {Problem}", printerId, problem.Key);

        _flashes.AddOrUpdate(printerId,
                             _ => throw new InvalidOperationException($"No flash of printer {printerId} to fail."),
                             (_, status) => status with { Stage = FirmwareFlashStage.Failed, Problem = problem });
    }
}
