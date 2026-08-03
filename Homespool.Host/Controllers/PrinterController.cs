using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// Everything done <em>to</em> a printer over the app API: send it one of your files, print
/// something already on it, and the six job-control verbs that act on whatever is already running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>PrinterTransferController</c></b> until the job-control verbs landed here on
/// 2026-07-28. They need <c>ResolveAsync</c> and <c>SendAsync</c>, which already lived here, and the
/// alternatives were refactoring working code or keeping a second copy of the outcome-to-status-code
/// mapping. That briefly left the class doing two things under a name covering one; renaming it
/// resolved that rather than leaving an apology in the summary.
/// </para>
/// <para>
/// <b>Uploads left for <see cref="PrintFileController"/> on 2026-07-31</b>, when files stopped being
/// something that happens to a printer and became a thing a user owns
/// (<c>notes/file-storage.md</c>). What is left here genuinely needs a printer in the route.
/// </para>
/// <para>
/// Transfer and print are deliberately <b>not</b> combined: a transfer takes as long as it takes and
/// a print starts instantly, so a single call would have to either block or lie about what it did.
/// Keeping them apart also means that when a rig run fails it is obvious which half broke. A
/// convenience call that does both is a later addition, and a pure one - nothing here needs rework
/// for it.
/// </para>
/// <para>
/// Cookie- or token-authenticated like <see cref="PrinterAppController"/>, so it is exercisable with
/// curl or a browser session - a personal access token is what makes the curl half pleasant
/// (notes/api-tokens.md). Permission is not checked here: the services this calls ask
/// <see cref="Authorisation.PrinterAccessService"/>, which is the one place that decides what an
/// account may do to a printer, and going around it would be a second answer to the same question.
/// That claim used to name <see cref="PrinterCommandService"/> and had quietly stopped being true -
/// six places were answering it by 2026-08-03.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrinterController : ControllerBase
{
    private readonly UserFileStore _files;
    private readonly PrintFileSender _sender;
    private readonly PrinterCommandService _commands;
    private readonly PrintStopService _stops;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<PrinterController> _logger;

    public PrinterController(UserFileStore files,
                             PrintFileSender sender,
                             PrinterCommandService commands,
                             PrintStopService stops,
                             PrinterQueryService printers,
                             UserManager<HSUser> userManager,
                             ILogger<PrinterController> logger)
    {
        _files = files;
        _sender = sender;
        _commands = commands;
        _stops = stops;
        _printers = printers;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Sends one of the caller's files to a printer. <c>POST /api/v1/printers/{uuid}/files</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Was <c>command/start/cloud</c></b>, which named where <i>Connect</i> kept the bytes. What
    /// actually distinguishes this from <see cref="Print"/> is whether the file has to be
    /// transferred first, so that is what the two routes are named for now
    /// (<c>notes/file-storage.md</c>). Gone with the old shape: <c>printNow</c>, which could only
    /// answer 501, and the body's <c>teamId</c>, which existed because the spec carried it - the
    /// team is a property of the printer and is read from it.
    /// </para>
    /// <para>
    /// <b>The transfer token is minted here and thrown away afterwards.</b> The printer quotes it
    /// back on the first range request of the transfer and never again, so it is correlation, not
    /// identity - which is exactly why the file's own name does not have to fit firmware's
    /// 28-character hash buffer, and why storage owes the wire nothing.
    /// </para>
    /// <para>
    /// Answers as soon as the printer accepts the command, which is not when the transfer finishes:
    /// the printer then pulls the bytes at its own pace over the same WebSocket, and a full-size
    /// model takes minutes. Watch for <c>TRANSFER_FINISHED</c>, or the transfer fields in telemetry.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("printers/{uuid:guid}/files")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SendFile(Guid uuid,
                                             [FromBody] SendFileRequest body,
                                             CancellationToken cancellationToken)
    {
        HSUser? caller = await _userManager.GetUserAsync(User);

        if (caller is null)
        {
            return Forbid();
        }

        // Scoped to the caller, so "someone else's file" and "no such file" are the same answer and
        // neither confirms the other's existence. This is the ownership check, and it is structural.
        StoredFile? file = _files.Find(caller.Id, body.Name);

        if (file is null)
        {
            return this.Failure(StatusCodes.Status404NotFound, $"You have no file named {body.Name}.");
        }

        if (file.Length >= uint.MaxValue)
        {
            // orig_size is uint32 on the wire; a file this large cannot be described at all.
            return this.Failure(StatusCodes.Status400BadRequest,
                "Files must be under 4 GiB - a printer cannot be sent anything larger.");
        }

        (Printer? printer, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        // Minting the token, offering the bytes and cleaning up after a send that did not take all
        // live in PrintFileSender, because the Files page needs exactly the same three things and
        // the cleanup rule is the one worth having a single copy of.
        const string wireName = "START_CONNECT_DOWNLOAD";

        try
        {
            CommandOutcome? outcome = await _sender.SendAsync(printer, file, caller.Id, cancellationToken);

            return outcome?.EventType is Events.Rejected or Events.Failed
                ? this.CommandFailure(StatusCodes.Status409Conflict, wireName,
                    outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString())
                : NoContent();
        }
        catch (PrintFileUnreadableException e)
        {
            return this.Failure(StatusCodes.Status409Conflict, e.Message);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogInformation(e, "{Command} to printer {PrinterId} did not complete", wireName, printer.Id);

            return this.CommandFailure(StatusCodes.Status409Conflict, wireName, e.Message);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Prints a file already on the printer. <c>POST /api/v1/printers/{uuid}/print</c>.
    /// </summary>
    /// <remarks>
    /// Takes a path on the <i>printer's</i> storage, not one of ours - a file can be on a printer
    /// without this server having put it there, which is exactly why sending and printing are
    /// separate calls. A file we sent is at the <c>printerPath</c> the file API reports.
    /// </remarks>
    [HttpPost]
    [Route("printers/{uuid:guid}/print")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Print(Guid uuid, [FromBody] PrintRequest body, CancellationToken cancellationToken)
    {
        if (!body.Path.StartsWith("/usb/", StringComparison.Ordinal) || body.Path.Contains("/../", StringComparison.Ordinal))
        {
            // The printer enforces this itself (path_allowed, planner.cpp:135-141); rejecting here
            // turns a silent refusal into an explanation.
            return this.Failure(StatusCodes.Status400BadRequest,
                "Path must be under /usb/ and contain no '/../' segment.");
        }

        (Printer? printer, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        return await SendAsync(printer, new StartPrint { Path = body.Path }, cancellationToken);
    }

    /// <summary>
    /// Lists what is on the printer's own storage.
    /// <c>GET /api/v1/printers/{uuid}/storage/usb/{path}</c>, with an empty path meaning the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>usb</c> is literal, because it is the only storage that exists.</b> Firmware hard-codes
    /// it in both directions: <c>path_allowed</c> accepts nothing else (planner.cpp:135-141), and the
    /// <c>storages</c> array in <c>INFO</c> writes the mountpoint as the constant <c>"/usb"</c>
    /// (render.cpp:353-366). A route segment that could only ever hold one value is better as that
    /// value, so a wrong root is a 404 from routing rather than a command spent earning
    /// <c>"Forbidden path"</c>.
    /// </para>
    /// <para>
    /// <b>Percent signs in names are a real trap, not a theoretical one.</b> A captured listing
    /// contains <c>wavy%20vase%20wide_0.4n_0.2mm_PLA_COREONE_1h56m.bgcode</c> - a literal
    /// <c>%20</c> in the filename - so a client must send it as <c>%2520</c> or this resolves it to
    /// a name with spaces and asks the printer about a file that does not exist.
    /// </para>
    /// <para>
    /// Either name works in the path: firmware resolves long names as well as 8.3 aliases, since
    /// FAT32 long-name support is live on <c>/usb</c>. Its <i>answer</i> is always in the 8.3 form
    /// though - see <see cref="PrinterStorageReadDTO.Path"/>.
    /// </para>
    /// <para>
    /// Gated on <c>CanUse</c> rather than <c>CanRead</c>, because although this reads, it does so by
    /// making the printer go and work: <see cref="PrinterCommandService"/> is the one place that
    /// decides, and it decides the same way for every command.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("printers/{uuid:guid}/storage/usb/{**path}")]
    [ProducesResponseType<PrinterStorageReadDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<PrinterStorageReadDTO>> Storage(Guid uuid, string? path, CancellationToken cancellationToken)
    {
        // Firmware rejects traversal itself; catching it here means an attempt never reaches the
        // printer and the caller gets told why, rather than spending a command on a refusal.
        if (path is not null && (path.Contains("/../", StringComparison.Ordinal) ||
                                 path.StartsWith("../", StringComparison.Ordinal) ||
                                 path.EndsWith("/..", StringComparison.Ordinal) ||
                                 path == ".."))
        {
            return this.Failure(StatusCodes.Status400BadRequest, "Path must contain no '/../' segment.");
        }

        (Printer? printer, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        string trimmed = path?.Trim('/') ?? string.Empty;
        SendFileInfo command = new() { Path = trimmed.Length == 0 ? "/usb" : $"/usb/{trimmed}" };
        HSUser user = (await _userManager.GetUserAsync(User))!;

        try
        {
            CommandOutcome<FileInfoEventDataDTO>? outcome =
                await _commands.AskAsync(printer.Id, command, user.Id, cancellationToken);

            if (outcome?.EventType is Events.Rejected or Events.Failed)
            {
                // Firmware answers a path that does not exist and a path it will not touch with the
                // same event, distinguished only by reason text - so this stays one status code and
                // hands the caller firmware's own words rather than guessing at a 404.
                return this.CommandFailure(StatusCodes.Status409Conflict, command.WireName,
                    outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString());
            }

            if (outcome?.Answer is null)
            {
                // Answered, and with nothing in it. Not a refusal and not unreadable - there is
                // simply no listing to return, and inventing an empty one would claim the storage is
                // empty when what happened is that the printer said nothing.
                _logger.LogInformation("{Command} to printer {PrinterId} answered {Outcome} with no data",
                    command.WireName, printer.Id, outcome?.EventType.ToString() ?? "nothing");

                return this.CommandFailure(StatusCodes.Status502BadGateway, command.WireName,
                    "The printer answered without a listing.");
            }

            return Ok(PrinterStorageReadDTO.FromEvent(outcome.Answer));
        }
        catch (CommandAnswerUnreadableException e)
        {
            // The printer answered and we could not read it, which is the gateway's failure and not
            // the caller's - so 502 rather than the 409 the transport failures below get.
            _logger.LogWarning(e, "{Command} to printer {PrinterId} answered unreadably", command.WireName, printer.Id);

            return this.CommandFailure(StatusCodes.Status502BadGateway, command.WireName, e.Message);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogInformation(e, "{Command} to printer {PrinterId} did not complete", command.WireName, printer.Id);

            return this.CommandFailure(StatusCodes.Status409Conflict, command.WireName, e.Message);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>Pauses a running print. <c>PUT /api/v1/printers/{uuid}/command/pause</c>.</summary>
    /// <remarks>
    /// The first of six job-control verbs, all named as Connect's own app API names them, all taking
    /// no body, and all answering the printer's real reply rather than an acknowledgement of the
    /// request - 204 when it accepted, 409 carrying its own rejection reason when it did not. The
    /// spec answers 200 with a <c>Command</c> resource instead; ours is the more useful shape and
    /// matches what <c>start/cloud</c> and <c>start/files</c> already do.
    /// <para>
    /// All six were verified against the real MK3.5 on 2026-07-24, before any of them had an endpoint
    /// - each sent while a genuine job was running, each answered with a real correlated event
    /// (AGENT-NOTES §3 item 3). What is new here is the route, not the command path.
    /// </para>
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/pause")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Pause(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, new PausePrint(), cancellationToken);
    }

    /// <summary>Resumes a paused print. <c>PUT /api/v1/printers/{uuid}/command/resume</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Resume(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, new ResumePrint(), cancellationToken);
    }

    /// <summary>Stops a running print. <c>PUT /api/v1/printers/{uuid}/command/stop</c>.</summary>
    /// <remarks>
    /// <b>The one job-control verb that does not go straight to <see cref="PrinterCommandService"/>.</b>
    /// A stop is the only one of the six whose cause cannot be recovered afterwards - the printer
    /// reports the same state change whoever asked - so it goes through
    /// <see cref="PrintStopService"/>, which notes who did before the answer comes back. Everything
    /// else about the call is unchanged, refusals included.
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Stop(Guid uuid, CancellationToken cancellationToken)
    {
        // Which printer, and whether this caller may be told it exists.
        (Printer? printer, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        // The one difference from the other five verbs: PrintStopService sends the same command
        // through the same permission gate, and notes who asked on the way past.
        return await SendAsync(printer, new StopPrint(), cancellationToken, send: _stops.StopAsync);
    }

    /// <summary>
    /// Marks the printer ready for a queued job. <c>PUT /api/v1/printers/{uuid}/command/ready</c>.
    /// </summary>
    /// <remarks>Answered <c>StateChanged</c> rather than <c>Finished</c> on hardware - both are
    /// success as far as this endpoint is concerned, since only Rejected and Failed are refusals.</remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/ready")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Ready(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, new SetPrinterReady(), cancellationToken);
    }

    /// <summary>Cancels the ready state. <c>PUT /api/v1/printers/{uuid}/command/unready</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/unready")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Unready(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, new CancelPrinterReady(), cancellationToken);
    }

    /// <summary>
    /// Returns the printer to idle. <c>PUT /api/v1/printers/{uuid}/command/idle</c>.
    /// </summary>
    /// <remarks>
    /// <b>The one route here that is ours rather than Connect's</b> - the spec has no equivalent, so
    /// the name is invented and deliberately follows the shape of its neighbours. The firmware only
    /// accepts it from the <c>Finished</c>/<c>Stopped</c> screen (<c>MarlinPrinter::set_idle</c>,
    /// marlin_printer.cpp:579-586); asked at any other moment it answers
    /// <c>Rejected {"Can't set idle now"}</c>, which arrives here as a 409 carrying that sentence.
    /// Both halves were seen on hardware.
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/idle")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Idle(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, new SetPrinterIdle(), cancellationToken);
    }

    /// <summary>Resolves the printer, then sends - the whole body of every job-control verb above.</summary>
    private async Task<ActionResult> SendJobControlAsync(Guid uuid,
                                                         ISendableCommand command,
                                                         CancellationToken cancellationToken)
    {
        (Printer? printer, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        return printer is null ? failure! : await SendAsync(printer, command, cancellationToken);
    }

    private async Task<(Printer? printer, ActionResult? failure)> ResolveAsync(Guid uuid,
                                                                                CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return (null, Forbid());
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, user.Id, cancellationToken);

        // Null covers both "no such printer" and "not visible to this user", deliberately - telling
        // them apart would confirm the existence of other people's printers.
        if (printer is null)
        {
            return (null, NotFound());
        }

        return (printer, null);
    }

    /// <summary>
    /// Sends a command and turns the printer's answer into a status code.
    /// </summary>
    /// <remarks>
    /// <b><c>send</c> is how it goes out</b>, for the one verb needing more than
    /// <see cref="PrinterCommandService"/> alone - see <see cref="Stop"/>. Null is the ordinary path,
    /// which is every other caller. A replacement throws the same exceptions and returns the same
    /// <see cref="CommandOutcome"/>, so nothing below that line has to know which one ran.
    /// </remarks>
    private async Task<ActionResult> SendAsync(Printer printer,
                                               ISendableCommand command,
                                               CancellationToken cancellationToken,
                                               Action? onFailure = null,
                                               Func<int, long, CancellationToken, Task<CommandOutcome?>>? send = null)
    {
        HSUser user = (await _userManager.GetUserAsync(User))!;

        try
        {
            CommandOutcome? outcome;

            if (send is null)
            {
                outcome = await _commands.SendCommandAsync(printer.Id, command, user.Id, cancellationToken);
            }
            else
            {
                outcome = await send(printer.Id, user.Id, cancellationToken);
            }

            // A null outcome is a command the printer cannot answer, written successfully - nothing
            // to inspect, and 204 is the honest result. None of this controller's commands are of
            // that kind today.
            if (outcome?.EventType is Events.Rejected or Events.Failed)
            {
                onFailure?.Invoke();

                return this.CommandFailure(StatusCodes.Status409Conflict, command.WireName,
                    outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString());
            }

            // 204, which is ours rather than the spec's - Connect documents 200 with a Command
            // resource for these. Answering the printer's real verdict is more useful to a caller
            // than an acknowledgement that we asked. The printer's actual reply is logged and
            // persisted as an ordinary event either way; a caller wanting it watches the event stream.
            return NoContent();
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            onFailure?.Invoke();
            _logger.LogInformation(e, "{Command} to printer {PrinterId} did not complete", command.WireName, printer.Id);

            return this.CommandFailure(StatusCodes.Status409Conflict, command.WireName, e.Message);
        }
        catch (TeamAccessDeniedException)
        {
            onFailure?.Invoke();

            return Forbid();
        }
    }

    /// <summary>Body of a send: which of the caller's files to transfer.</summary>
    public class SendFileRequest
    {
        /// <summary>The file's name, as the file API lists it.</summary>
        public required string Name { get; set; }
    }

    /// <summary>Body of a print: what to run, on the printer's own storage.</summary>
    public class PrintRequest
    {
        /// <summary>Path on the printer, under <c>/usb/</c>.</summary>
        public required string Path { get; set; }
    }
}
