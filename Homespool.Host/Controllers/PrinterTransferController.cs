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
/// Upload a file, send it to a printer, print it - as three separate calls.
/// </summary>
/// <remarks>
/// <para>
/// A testing surface, and shaped like one (Henrik, 2026-07-27). Transfer and print are deliberately
/// <b>not</b> combined: a transfer takes as long as it takes and a print starts instantly, so a
/// single call would have to either block or lie about what it did. Keeping them apart also means
/// that when a rig run fails it is obvious which half broke. A convenience call that does both is a
/// later addition, and a pure one - nothing here needs rework for it.
/// </para>
/// <para>
/// Cookie-authenticated like <see cref="PrinterAppController"/>, so it is exercisable with curl or a
/// browser session. Permission is not checked here: <see cref="PrinterCommandService"/> is the one
/// place that consults <c>TeamMember.CanUse</c>, and going around it would be a second answer to the
/// same question.
/// </para>
/// </remarks>
[Authorize]
[ApiController]
[Route("/api/v1")]
public class PrinterTransferController : ControllerBase
{
    private readonly UploadedFileStore _files;
    private readonly ITransferOffers _offers;
    private readonly PrinterCommandService _commands;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly FileStorageOptions _options;
    private readonly ILogger<PrinterTransferController> _logger;

    public PrinterTransferController(UploadedFileStore files, ITransferOffers offers,
        PrinterCommandService commands, PrinterQueryService printers, UserManager<HSUser> userManager,
        IOptions<FileStorageOptions> options, ILogger<PrinterTransferController> logger)
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
        // one command/start/cloud takes below.
        return Ok(new { hash = stored.Id, fileName = stored.FileName, length = stored.Length });
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
            Path = $"/usb/{file.FileName}",
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
            CommandOutcome outcome = await _commands.SendCommandAsync(printer.Id, command, user.Id, cancellationToken);

            if (outcome.EventType is Events.Rejected or Events.Failed)
            {
                onFailure?.Invoke();

                return StatusCode(StatusCodes.Status409Conflict,
                    new { command = command.WireName, outcome = outcome.EventType.ToString(), reason = outcome.Reason });
            }

            // 204, as the spec's command endpoints answer. The printer's actual reply is logged and
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
