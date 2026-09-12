using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages;

/// <summary>
/// Dropping a file onto a printer: the question a drop raises, and carrying out the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared by every page that draws printers as targets</b> - the front page's tiles and the
/// printers listing's cards - so the two cannot drift on what a drop means, which names clash, or
/// what may be offered. The pages keep the HTTP half: which printer, whether the reader may see it,
/// what to answer when they may not, and where a camera's frame is served from are results, URLs and
/// a principal, and belong to a page. This takes a printer already found and a caller already
/// resolved, and does the work.
/// </para>
/// <para>
/// <b>Per-file reporting, because upload-then-queue can half-succeed.</b> A file can land and then be
/// refused a place in the queue - a hold, a nozzle mismatch, a model incompatibility - so one summary
/// line would be a lie for whichever half failed. Each file gets its own sentence.
/// </para>
/// <para>
/// <b>A list, though a drag only ever sends one.</b> The single-file rule lives in tile-drop.js,
/// where the drop happens; the handlers are endpoints anybody can post to, and looping is what keeps
/// them honest rather than dependent on the caller having obeyed a convention they cannot see.
/// </para>
/// <para>
/// <b>Readying happens last, and only if something was queued.</b> Making a printer ready with
/// nothing at the head of its queue offers the machine up for work that is not there; doing it first
/// would let the loop pick up an unrelated older entry the moment it went ready, which is a print
/// nobody asked for starting because of a drop that then failed. <b>And there is no "start printing"
/// command behind it, nor should there be</b>: <see cref="QueueRules.IsAvailable"/> admits exactly
/// one status, <c>Ready</c>, and the advancer picks the head up within about a second of it, so
/// readying a printer whose queue this drop just filled <i>is</i> printing now. A direct start would
/// be a second path to the same place that skipped the rule standing between a queue and a print
/// onto somebody's finished part.
/// </para>
/// <para>
/// <b>Ready is still guarded here</b> even though the dialog only offers it when allowed and the page
/// refuses it for a printer that does not permit remote readying. The browser decides what to show;
/// it does not decide what may happen.
/// </para>
/// </remarks>
public sealed class TileDrop
{
    /// <summary>Upload only: the bytes land in the reader's tree and nothing else happens.</summary>
    public const string Upload = "upload";

    /// <summary>Upload, then join the printer's queue.</summary>
    public const string Queue = "queue";

    /// <summary>Upload, queue, and offer the printer up for work.</summary>
    public const string ReadyAndPrint = "ready";

    private readonly PrintQueueService _queue;
    private readonly PrinterAccessService _access;
    private readonly PrintFileCatalog _files;
    private readonly CameraAccessService _cameras;
    private readonly PrintFileStorageOptions _storage;
    private readonly PrinterCommandService _commands;
    private readonly PrinterConnectionRegistry _connections;
    private readonly PrinterIntentText _intents;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ErrorText _errors;

    public TileDrop(PrintQueueService queue,
                    PrinterAccessService access,
                    PrintFileCatalog files,
                    CameraAccessService cameras,
                    IOptionsSnapshot<PrintFileStorageOptions> storage,
                    PrinterCommandService commands,
                    PrinterConnectionRegistry connections,
                    PrinterIntentText intents,
                    IStringLocalizer<SharedResource> localiser,
                    ErrorText errors)
    {
        _queue = queue;
        _access = access;
        _files = files;
        _cameras = cameras;
        _storage = storage.Value;
        _commands = commands;
        _connections = connections;
        _intents = intents;
        _localiser = localiser;
        _errors = errors;
    }

    /// <summary>Whether an action ends by offering the printer up for work.</summary>
    public static bool Readies(string action)
    {
        return action == ReadyAndPrint;
    }

    /// <summary>
    /// The camera whose still the bed-clear question shows, or null when the printer has none.
    /// </summary>
    /// <remarks>
    /// The first camera, matching what the printer page's Set ready modal shows. A printer with two
    /// cameras has one that answers "is the sheet clear" better than the other, and nothing here
    /// knows which - so this takes the same one that page takes rather than inventing a preference.
    /// The page turns it into a URL; routing is its business.
    /// </remarks>
    public async Task<Guid?> FirstCameraAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        IReadOnlyList<Camera> cameras = await _cameras.ListForPrinterAsync(printerId, caller, cancellationToken);

        return cameras.Count == 0 ? null : cameras[0].Uuid;
    }

    /// <summary>
    /// Answers what a drop would collide with, and what may be offered, before it uploads anything.
    /// </summary>
    /// <remarks>
    /// <b>Names in, a rendered dialog out</b> - the page renders the prompt as a partial. It could
    /// answer JSON and let the browser build the dialog, and then every word in it would need a second
    /// copy of the vocabulary out there - the same trade <c>live-region.js</c> refuses at the top of
    /// its file.
    /// </remarks>
    public async Task<TileDropPrompt> PromptAsync(PrinterWithState row,
                                                  Caller caller,
                                                  IReadOnlyList<string> names,
                                                  string? cameraFrameUrl,
                                                  CancellationToken cancellationToken)
    {
        // The reader's own tree, so the comparison never sees anybody else's names. Ordinal-ignore-case
        // because that is what the store treats as the same file.
        HashSet<string> existing = _files.List(caller)
                                         .Select(stored => stored.FileName)
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool canPrint = await _access.AllowsAsync(row.Printer.Id, caller, Capability.Print, cancellationToken);

        return new TileDropPrompt(
            row.Printer.Uuid,
            PrinterDisplayName.For(row.Printer),
            [.. names.Select(name => new TileDropFile(name,
                                     existing.Contains(name),
                                     UserFileStore.IsAllowedExtension(name)))],
            canPrint,
            canPrint && row.Printer.RemoteReadyAllowed && _connections.IsConnected(row.Printer.Id),
            caller.Allows(Capability.ManipulateOwnFiles),
            cameraFrameUrl,
            string.Join(", ", UserFileStore.AllowedExtensions),
            ByteSize.Format(_storage.MaxUploadBytes, _localiser));
    }

    /// <summary>
    /// Carries out a drop: upload each file, then queue, then optionally make the printer ready. Says
    /// what happened, per file, and whether all of it was good news.
    /// </summary>
    public async Task<(string message, bool success)> DropAsync(PrinterWithState row,
                                                                Caller caller,
                                                                string action,
                                                                IReadOnlyList<IFormFile> files,
                                                                IReadOnlyList<string> replace,
                                                                string? userName,
                                                                CancellationToken cancellationToken)
    {
        bool queueing = action is Queue or ReadyAndPrint;
        bool readying = Readies(action);

        HashSet<string> replacing = replace.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> report = [];
        int queued = 0;
        bool refused = false;

        foreach (IFormFile file in files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            string? stored = await StoreAsync(caller, file, replacing.Contains(file.FileName), userName, report, cancellationToken);

            if (stored is null)
            {
                continue;
            }

            if (!queueing)
            {
                // Said out loud, because an upload-only drop otherwise finishes in silence and looks
                // exactly like a drop that missed the tile. StoreAsync only reports what went wrong.
                report.Add(_localiser["Files_UploadedFile", stored].Value);

                continue;
            }

            try
            {
                await _queue.EnqueueAsync(row.Printer.Id, caller, stored, cancellationToken);
                queued++;
                report.Add(_localiser["Home_DropQueued", stored, PrinterDisplayName.For(row.Printer)].Value);
            }
            catch (Exception e) when (e is ILocalisableError)
            {
                // The bytes are safely in the reader's tree either way, so this reports the queue's
                // refusal and leaves the file alone rather than undoing an upload that was fine.
                report.Add(_localiser["Home_DropQueueRefused", stored, _errors.For(e)].Value);
            }
        }

        if (readying && queued > 0)
        {
            try
            {
                SetPrinterReady intent = new();
                CommandOutcome? outcome =
                    await _commands.SendCommandAsync(row.Printer.Id, intent, caller, cancellationToken);

                if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
                {
                    // A refusal is an answer, not an exception, so it does not reach the catch below
                    // and used to be reported as success - on a drop whose whole point is that the
                    // file starts printing. Firmware declines the flag from anything busy, which is
                    // exactly when somebody drops a file onto a printer already working.
                    report.Add(_localiser["Printers_CommandRejected",
                                          _intents.For(intent),
                                          outcome!.Reason ?? string.Empty].Value);
                    refused = true;
                }
                else
                {
                    // The printer page's own words for this, unchanged: the queue line above already
                    // named the printer, so repeating it here would be a second voice.
                    report.Add(_localiser["Printers_ReadySent"].Value);
                }
            }
            catch (Exception e) when (e is ILocalisableError)
            {
                // The upload and the queue already happened and are worth keeping - this is the last
                // step of three, and losing the first two because the third failed would be worse
                // than saying so. Uncaught, it escaped as a 500 and the person got a blank page
                // having no idea their file had in fact been queued.
                report.Add(_errors.For(e));
                refused = true;
            }
        }

        // A drop where every file was refused before it started - all of them the wrong kind, or an
        // empty selection - would otherwise redirect to a page saying nothing at all.
        string message = report.Count > 0 ? string.Join(" ", report) : _localiser["Home_DropNothing"].Value;
        bool success = report.Count > 0 && !refused && (queued > 0 || !queueing);

        return (message, success);
    }

    /// <summary>
    /// Puts one dropped file in the reader's tree, reporting what happened to it.
    /// </summary>
    /// <remarks>
    /// Staged then published, the same two steps the Files page uses. The name clash was settled
    /// before any of this ran, so <paramref name="overwrite"/> is an answer already given rather than
    /// a question asked here - but the store is still the authority, and a file that appeared between
    /// the question and now comes back as a conflict and is reported rather than silently replaced.
    /// </remarks>
    private async Task<string?> StoreAsync(Caller caller,
                                           IFormFile file,
                                           bool overwrite,
                                           string? userName,
                                           List<string> report,
                                           CancellationToken cancellationToken)
    {
        PendingUpload staged;

        try
        {
            await using Stream content = file.OpenReadStream();

            staged = await _files.StageAsync(caller, file.FileName, content, cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or ILocalisableError)
        {
            report.Add(_localiser["Home_DropRejected", file.FileName, _errors.For(e)].Value);

            return null;
        }

        try
        {
            StoredFile? published = await _files.PublishAsync(caller, staged.Token, overwrite, cancellationToken, userName: userName);

            return published?.FileName;
        }
        catch (PrintFileNameConflictException)
        {
            // Keep the file already on disk, and throw away the bytes just staged rather than leaving
            // them to age out - the reader answered this question before the upload started, so there
            // is nothing left to ask and nothing to keep them for.
            _files.Discard(caller, staged.Token);

            report.Add(_localiser["Home_DropKept", file.FileName].Value);

            return staged.FileName;
        }
    }
}
