using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Controllers;

/// <summary>
/// Everything done <em>to</em> a printer over the app API: upload a file, send it to a printer,
/// print it - as three separate calls - plus the six job-control verbs that act on whatever is
/// already running.
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
/// A testing surface, and shaped like one (Henrik, 2026-07-27). Transfer and print are deliberately
/// <b>not</b> combined: a transfer takes as long as it takes and a print starts instantly, so a
/// single call would have to either block or lie about what it did. Keeping them apart also means
/// that when a rig run fails it is obvious which half broke. A convenience call that does both is a
/// later addition, and a pure one - nothing here needs rework for it.
/// </para>
/// <para>
/// Cookie- or token-authenticated like <see cref="PrinterAppController"/>, so it is exercisable with
/// curl or a browser session - a personal access token is what makes the curl half pleasant
/// (notes/api-tokens.md). Permission is not checked here: <see cref="PrinterCommandService"/> is the one
/// place that consults <c>TeamMember.CanUse</c>, and going around it would be a second answer to the
/// same question.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorization.Policies.Api)]
public class PrinterController : ControllerBase
{
    private readonly UploadedFileStore _files;
    private readonly ITransferOffers _offers;
    private readonly PrinterCommandService _commands;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly FileStorageOptions _options;
    private readonly ILogger<PrinterController> _logger;

    public PrinterController(UploadedFileStore files, ITransferOffers offers,
        PrinterCommandService commands, PrinterQueryService printers, UserManager<HSUser> userManager,
        IOptions<FileStorageOptions> options, ILogger<PrinterController> logger)
    {
        _files = files;
        _offers = offers;
        _commands = commands;
        _printers = printers;
        _userManager = userManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a file, raw. <c>PUT /api/v1/files/{fileName}</c> with the bytes as the body.
    /// </summary>
    /// <remarks>
    /// Raw body rather than multipart, matching PrusaLink's own upload shape
    /// (<c>PUT /api/v1/files/{storage}/{path}</c>, notes/transfer-protocol.md) and trivially
    /// curl-able: <c>curl -T model.bgcode .../api/v1/files/model.bgcode</c>. It also streams
    /// straight to disk, where multipart would buffer a few hundred megabytes first.
    /// </remarks>
    [HttpPut]
    [Route("files/{fileName}")]
    [RequestSizeLimit(long.MaxValue)] // Enforced against the configured cap below, not by MVC.
    public async Task<IActionResult> Upload(string fileName, CancellationToken cancellationToken)
    {
        if (!UploadedFileStore.IsAllowedExtension(fileName))
        {
            return BadRequest("Only .gcode, .bgcode, .gco and .bgc are accepted - the printer would "
                            + "refuse anything else after the transfer.");
        }

        // Content-Length is advisory (a client may omit it), so this rejects the obvious case early
        // and the copy below is still bounded.
        if (Request.ContentLength > _options.MaxUploadBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }

        StoredFile stored;

        try
        {
            await using LengthLimitingStream limited = new(Request.Body, _options.MaxUploadBytes);
            stored = await _files.SaveAsync(fileName, limited, cancellationToken);
        }
        catch (UploadTooLargeException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        // "hash" rather than "id": it is the same value Connect's app API calls a hash, and the same
        // one command/start/cloud takes below. "printerPath" is what command/start/files will need
        // once the transfer has run - reported here because the server owns that convention and had
        // no way of telling anyone what it was.
        return Ok(new
        {
            hash = stored.Id,
            fileName = stored.FileName,
            length = stored.Length,
            printerPath = stored.PrinterPath,
        });
    }

    /// <summary>
    /// Tells a printer to fetch a file we hold. <c>PUT /api/v1/printers/{uuid}/command/start/cloud</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modelled on Connect's own app API, which takes the same <c>{hash, teamId, printNow}</c> for
    /// this operation (<c>docs/prusa-connect-mobile-app.jsonopenapi</c>). "Cloud" there means files
    /// Connect holds, as opposed to <c>start/files</c> for files already on the printer - the same
    /// distinction this server has, so the shape transfers directly.
    /// </para>
    /// <para>
    /// Answers as soon as the printer accepts the command, which is not when the transfer finishes:
    /// the printer then pulls the bytes at its own pace over the same WebSocket, and a full-size
    /// model takes minutes. Watch for <c>TRANSFER_FINISHED</c>, or the transfer fields in telemetry.
    /// </para>
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/start/cloud")]
    public async Task<IActionResult> StartFromCloud(Guid uuid, [FromBody] StartFromCloudRequest body, CancellationToken cancellationToken)
    {
        if (body.PrintNow == true)
        {
            // Printing on completion means firing START_PRINT when TRANSFER_FINISHED arrives, which
            // the actor does not do yet. Refusing is honest; silently transferring without printing
            // would be worse than not accepting the field at all.
            return StatusCode(StatusCodes.Status501NotImplemented,
                "printNow is not implemented - transfer with printNow false, then call command/start/files.");
        }

        StoredFile? file = _files.Find(body.Hash);

        if (file is null)
        {
            return NotFound($"No uploaded file with hash {body.Hash}.");
        }

        if (file.Length >= uint.MaxValue)
        {
            // orig_size is uint32 on the wire; a file this large cannot be described at all.
            return BadRequest("File is too large to describe to a printer (4 GiB limit).");
        }

        (Printer? printer, IActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        if (body.TeamId != printer.TeamId)
        {
            // The spec sends the team explicitly and we could derive it, but disagreeing about which
            // team owns a printer means the caller is working from a stale view - better to say so
            // than to act on our version of it.
            return BadRequest($"Printer belongs to team {printer.TeamId}, not {body.TeamId}.");
        }

        // The file's own id is the transfer token: the printer quotes it back on the first range
        // request, and that is how the actor finds these bytes again. One identifier, as in Connect.
        _offers.Offer(file.Id, file.Path);

        StartConnectDownload command = new()
        {
            Path = file.PrinterPath,
            Hash = file.Id,
            TeamId = (ulong)printer.TeamId,
            OriginalSize = file.Length,
        };

        return await SendAsync(printer, command, cancellationToken, onFailure: () => _offers.Revoke(file.Id));
    }

    /// <summary>
    /// Prints a file already on the printer. <c>PUT /api/v1/printers/{uuid}/command/start/files</c>.
    /// </summary>
    /// <remarks>
    /// Connect's app API spells this the same way, with the same <c>{path, printNow}</c> body. Takes
    /// a path on the <i>printer's</i> storage, not one of our hashes - a file can be on a printer
    /// without this server having put it there, which is exactly why the two start operations are
    /// separate.
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/start/files")]
    public async Task<IActionResult> StartFromPrinterFiles(Guid uuid, [FromBody] StartFromFilesRequest body, CancellationToken cancellationToken)
    {
        if (body.PrintNow == false)
        {
            // The file is already on the printer, so there is nothing to do but print it. The spec
            // carries the flag on both start operations; here only true means anything.
            return BadRequest("printNow false is meaningless for a file already on the printer.");
        }

        if (!body.Path.StartsWith("/usb/", StringComparison.Ordinal) || body.Path.Contains("/../", StringComparison.Ordinal))
        {
            // The printer enforces this itself (path_allowed, planner.cpp:135-141); rejecting here
            // turns a silent refusal into an explanation.
            return BadRequest("Path must be under /usb/ and contain no '/../' segment.");
        }

        (Printer? printer, IActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        return await SendAsync(printer, new StartPrint { Path = body.Path }, cancellationToken);
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
    public Task<IActionResult> Pause(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new PausePrint(), cancellationToken);

    /// <summary>Resumes a paused print. <c>PUT /api/v1/printers/{uuid}/command/resume</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/resume")]
    public Task<IActionResult> Resume(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new ResumePrint(), cancellationToken);

    /// <summary>Stops a running print. <c>PUT /api/v1/printers/{uuid}/command/stop</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/stop")]
    public Task<IActionResult> Stop(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new StopPrint(), cancellationToken);

    /// <summary>
    /// Marks the printer ready for a queued job. <c>PUT /api/v1/printers/{uuid}/command/ready</c>.
    /// </summary>
    /// <remarks>Answered <c>StateChanged</c> rather than <c>Finished</c> on hardware - both are
    /// success as far as this endpoint is concerned, since only Rejected and Failed are refusals.</remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/ready")]
    public Task<IActionResult> Ready(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new SetPrinterReady(), cancellationToken);

    /// <summary>Cancels the ready state. <c>PUT /api/v1/printers/{uuid}/command/unready</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/unready")]
    public Task<IActionResult> Unready(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new CancelPrinterReady(), cancellationToken);

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
    public Task<IActionResult> Idle(Guid uuid, CancellationToken cancellationToken) =>
        SendJobControlAsync(uuid, new SetPrinterIdle(), cancellationToken);

    /// <summary>Resolves the printer, then sends - the whole body of every job-control verb above.</summary>
    private async Task<IActionResult> SendJobControlAsync(Guid uuid, ISendableCommand command, CancellationToken cancellationToken)
    {
        (Printer? printer, IActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        return printer is null ? failure! : await SendAsync(printer, command, cancellationToken);
    }

    private async Task<(Printer? printer, IActionResult? failure)> ResolveAsync(Guid uuid, CancellationToken cancellationToken)
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
        return printer is null ? (null, NotFound()) : (printer, null);
    }

    private async Task<IActionResult> SendAsync(Printer printer, ISendableCommand command,
        CancellationToken cancellationToken, Action? onFailure = null)
    {
        HSUser user = (await _userManager.GetUserAsync(User))!;

        try
        {
            CommandOutcome? outcome = await _commands.SendCommandAsync(printer.Id, command, user.Id, cancellationToken);

            // A null outcome is a command the printer cannot answer, written successfully - nothing
            // to inspect, and 204 is the honest result. None of this controller's commands are of
            // that kind today.
            if (outcome?.EventType is Events.Rejected or Events.Failed)
            {
                onFailure?.Invoke();

                return StatusCode(StatusCodes.Status409Conflict,
                    new { command = command.WireName, outcome = outcome.EventType.ToString(), reason = outcome.Reason });
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

            return StatusCode(StatusCodes.Status409Conflict, new { command = command.WireName, error = e.Message });
        }
        catch (TeamAccessDeniedException)
        {
            onFailure?.Invoke();

            return Forbid();
        }
    }
}

/// <summary>
/// Body of <c>command/start/cloud</c>, matching Connect's app API.
/// </summary>
public class StartFromCloudRequest
{
    /// <summary>The uploaded file's id, which is also its transfer token.</summary>
    public required string Hash { get; set; }

    /// <summary>The team the caller believes owns the printer; validated, not trusted.</summary>
    public required long TeamId { get; set; }

    /// <summary>Print once the transfer completes. Not implemented - see the endpoint.</summary>
    public bool? PrintNow { get; set; }
}

/// <summary>Body of <c>command/start/files</c>, matching Connect's app API.</summary>
public class StartFromFilesRequest
{
    /// <summary>Path on the printer's own storage, under <c>/usb/</c>.</summary>
    public required string Path { get; set; }

    /// <summary>Only <c>true</c> is meaningful here; the file is already present.</summary>
    public bool? PrintNow { get; set; }
}
