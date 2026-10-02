using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Which version of a file a printer's drive holds, and replacing an older one before the current
/// one is sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place for the queue and both direct sends</b>, as <see cref="PrinterDriveNames"/> is for the
/// name. An overwrite keeps the file's row and changes its digest while the drive keeps the old bytes
/// under the same name, and the printer refuses a transfer onto a name already taken - so every send
/// has to clear an older copy first, or the newer version can never get there.
/// </para>
/// <para>
/// <b>Only a copy Homespool can vouch for is deleted</b>: one whose
/// <see cref="PrintFileOnPrinter.Digest"/> it recorded when the printer took the transfer. A file
/// found under the name with no digest beside it may be anybody's, and is left where it is.
/// </para>
/// </remarks>
public sealed class PrinterDriveCopies
{
    /// <summary>Firmware's answer for a path with nothing at it (planner.cpp:887-888).</summary>
    private const string NotFound = "File not found";

    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterCommandService _commands;
    private readonly PrintFileCatalog _catalog;
    private readonly PrinterDriveNames _driveNames;
    private readonly PrintFileSender _sender;
    private readonly ILogger<PrinterDriveCopies> _logger;

    public PrinterDriveCopies(HomespoolDbContext dbContext,
                              PrinterCommandService commands,
                              PrintFileCatalog catalog,
                              PrinterDriveNames driveNames,
                              PrintFileSender sender,
                              ILogger<PrinterDriveCopies> logger)
    {
        _dbContext = dbContext;
        _commands = commands;
        _catalog = catalog;
        _driveNames = driveNames;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>
    /// Whether the copy <paramref name="row"/> describes is the version of the file whose digest is
    /// <paramref name="digest"/>.
    /// </summary>
    /// <remarks>
    /// False whenever either side is unknown: a copy nobody can vouch for is not the file, and a file
    /// with no digest has nothing a copy could match.
    /// </remarks>
    public static bool IsCurrent(PrintFileOnPrinter? row, string? digest)
    {
        return row?.Digest is { } held && digest is not null && string.Equals(held, digest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records that the printer took a transfer of the bytes whose digest is <paramref name="digest"/>
    /// to the row's name. Not saved.
    /// </summary>
    /// <remarks>
    /// <b>Whatever the row said had arrived is forgotten with it</b>: the drive now holds a transfer of
    /// these bytes in progress, not what was there before, and only its own end says it has arrived.
    /// </remarks>
    public static void RecordTaken(PrintFileOnPrinter row, string digest)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Digest = digest;
        row.ArrivedAt = null;
    }

    /// <summary>
    /// Deletes the older copy under the row's name when there is one Homespool sent, and forgets it.
    /// </summary>
    /// <param name="printerId">The printer whose drive it is.</param>
    /// <param name="row">The file's row on that printer, tracked by this scope's context.</param>
    /// <param name="digest">The digest of the version about to be sent.</param>
    /// <param name="caller">The authority the send is made under, which the delete borrows.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <remarks>
    /// <para>
    /// <b><c>File not found</c> is success</b> - somebody deleted it at the panel, or a card was
    /// swapped - and so is anything that is not a refusal, because firmware answers a delete with
    /// <c>FILE_CHANGED</c> rather than a verdict.
    /// </para>
    /// <para>
    /// <b>In use is told apart from refused</b>, because only one of them goes away by itself: a print
    /// of the old copy ends, and a transfer of it finishes. Firmware says <c>File is busy</c> and
    /// <c>File is being transferred</c>; the Python SDK says <c>File is read only</c> and <c>This file
    /// is currently printed</c>. Classified on the words because neither sends a machine reason for a
    /// delete. The SDK also has no answer for a missing file - it fails on the lookup - so on that
    /// client a copy deleted by hand reads as refused.
    /// </para>
    /// <para>
    /// Anything that is not an answer - not connected, no reply in time - propagates, as from a send:
    /// it says nothing about the copy, and the caller already handles those for the send that follows.
    /// </para>
    /// </remarks>
    public async Task<OutdatedCopyOutcome> RemoveOutdatedAsync(int printerId,
                                                               PrintFileOnPrinter row,
                                                               string digest,
                                                               Caller caller,
                                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Digest is null || row.DriveName is null || IsCurrent(row, digest))
        {
            return new OutdatedCopyOutcome(OutdatedCopyRemoval.NothingToRemove);
        }

        string path = PrinterDriveNames.OnDrive(row.DriveName);
        CommandOutcome? outcome = await _commands.SendCommandAsync(printerId, new DeleteFile { Path = path }, caller,
                                                                   cancellationToken);

        if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed && outcome.Reason != NotFound)
        {
            OutdatedCopyRemoval kept = outcome.Reason is "File is busy" or "File is being transferred" or
                                                         "File is read only" or "This file is currently printed" ?
                OutdatedCopyRemoval.InUse :
                OutdatedCopyRemoval.Refused;

            _logger.LogInformation("[{PrinterId}] kept the older copy of {DriveName}: {Reason}",
                                   printerId, row.DriveName, LogText.Clean(outcome.Reason));

            return new OutdatedCopyOutcome(kept, outcome.Reason);
        }

        _logger.LogInformation("[{PrinterId}] deleted the older copy of {DriveName} to send the current one",
                               printerId, row.DriveName);

        row.Digest = null;
        row.ArrivedAt = null;
        row.PrinterPath = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new OutdatedCopyOutcome(OutdatedCopyRemoval.Removed);
    }

    /// <summary>
    /// Sends one of a user's files to a printer outside the queue: names it there, clears an older
    /// copy of it, sends it, and records what was sent.
    /// </summary>
    /// <param name="printer">The printer, already resolved and authorised by the caller.</param>
    /// <param name="indexed">The file's row.</param>
    /// <param name="file">The file's bytes, as the store holds them.</param>
    /// <param name="caller">The authority the send is made under.</param>
    /// <param name="cancellationToken">Cancels the send, not a transfer the printer has accepted.</param>
    /// <exception cref="PrintFileUnreadableException">The file could not be read.</exception>
    /// <remarks>
    /// <para>
    /// <b>The API and the Files page both send this way</b>, and the part worth having once is the
    /// order: the digest is settled before anything is offered, the older copy is gone before the
    /// transfer is asked for, and the digest is recorded only once the printer has taken it.
    /// </para>
    /// <para>
    /// <b>A response that never came still records the digest</b>, because the printer may well be
    /// fetching - firmware acknowledges a download late when it is busy - and a copy of these bytes
    /// arriving with an older digest beside it would be deleted and sent again for nothing.
    /// </para>
    /// </remarks>
    public async Task<DirectSendResult> SendAsync(Printer printer,
                                                  PrintFile indexed,
                                                  StoredFile file,
                                                  Caller caller,
                                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);

        // The name on the printer's drive is reserved before the send, as the queue reserves it, so
        // the next transfer there - queued or direct, anyone's - sees it taken.
        PrintFileOnPrinter row = await _driveNames.ReserveAsync(printer.Id, indexed, cancellationToken);
        string digest = await _catalog.DigestForSendingAsync(indexed, file, cancellationToken);

        OutdatedCopyOutcome cleared = await RemoveOutdatedAsync(printer.Id, row, digest, caller, cancellationToken);

        if (!cleared.Cleared)
        {
            return new DirectSendResult(cleared, Sent: null);
        }

        FileSendResult sent;

        try
        {
            sent = await _sender.SendAsync(printer, file, PrinterDriveNames.OnDrive(row.DriveName!), caller, cancellationToken);
        }
        catch (CommandResponseTimedOutException)
        {
            RecordTaken(row, digest);
            await _dbContext.SaveChangesAsync(cancellationToken);

            throw;
        }

        if (sent.Outcome?.EventType is not (PrinterEventType.Rejected or PrinterEventType.Failed))
        {
            RecordTaken(row, digest);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return new DirectSendResult(cleared, sent);
    }
}
