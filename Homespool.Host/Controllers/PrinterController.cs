using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
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
/// (notes/api-tokens.md). Permission is not checked here: <see cref="PrinterCommandService"/> is the one
/// place that consults <c>TeamMember.CanUse</c>, and going around it would be a second answer to the
/// same question.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]
public class PrinterController : ControllerBase
{
    /// <summary>
    /// Bytes of randomness in a transfer token. 21 rather than 20 because base64url carries three
    /// bytes per four characters, so 21 encodes to exactly 28 - filling firmware's hash buffer
    /// (<see cref="StartConnectDownload.MaxHashLength"/>) with nothing left over and no padding.
    /// </summary>
    /// <remarks>
    /// Unguessable is not load-bearing here - the token is only meaningful to the printer that was
    /// just told to use it, and ownership is enforced before one is ever minted. It is random
    /// because there is no reason for it to be anything else, and 168 bits is what the space
    /// happened to be.
    /// </remarks>
    private const int TransferTokenBytes = 21;

    private readonly UserFileStore _files;
    private readonly ITransferOffers _offers;
    private readonly PrinterCommandService _commands;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<PrinterController> _logger;

    public PrinterController(UserFileStore files,
                                    ITransferOffers offers,
                                    PrinterCommandService commands,
                                    PrinterQueryService printers,
                                    UserManager<HSUser> userManager,
                                    ILogger<PrinterController> logger)
    {
        _files = files;
        _offers = offers;
        _commands = commands;
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
    public async Task<IActionResult> SendFile(Guid uuid, [FromBody] SendFileRequest body, CancellationToken cancellationToken)
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
            return NotFound($"You have no file named {body.Name}.");
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

        string token = TransferToken();

        // Opens the file, which is what pins these bytes for the transfer - see ITransferOffers. It
        // failing means the file went away between being found and being offered, which is a delete
        // racing this send rather than anything the caller did wrong.
        if (!_offers.Offer(token, file.Path))
        {
            return Conflict($"{file.FileName} could not be read - it may have just been deleted.");
        }

        StartConnectDownload command = new()
        {
            Path = file.PrinterPath,
            Hash = token,
            TeamId = (ulong)printer.TeamId,
            OriginalSize = file.Length,
        };

        return await SendAsync(printer, command, cancellationToken, onFailure: () => _offers.Revoke(token));
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
    public async Task<IActionResult> Print(Guid uuid, [FromBody] PrintRequest body, CancellationToken cancellationToken)
    {
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

    /// <summary>A fresh transfer token: 21 random bytes, base64url'd to firmware's full 28.</summary>
    private static string TransferToken() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TransferTokenBytes));

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
