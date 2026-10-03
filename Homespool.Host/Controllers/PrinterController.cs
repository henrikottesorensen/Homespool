using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

// What the six job-control verbs answer, and the two helpers behind them: named once, because a
// union spelled out at seven sites is seven chances for one to drift.
using JobControlResult =
    Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.NoContent,
        Homespool.Host.Controllers.ForbiddenProblem,
        Homespool.Host.Controllers.NotFoundProblem,
        Homespool.Host.Controllers.ConflictProblem>;

namespace Homespool.Host.Controllers;

/// <summary>
/// Everything done <em>to</em> a printer over the app API: send it one of your files, browse its
/// storage, and the job-control verbs that act on whatever is already running. Starting a print is
/// not here - that is the queue's, which waits for a person to ready the printer.
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
/// something that happens to a printer and became a thing a user owns. What is left here genuinely
/// needs a printer in the route.
/// </para>
/// <para>
/// Cookie- or token-authenticated like <see cref="PrinterAppController"/>, so it is exercisable with
/// curl or a browser session - a personal access token is what makes the curl half pleasant.
/// Permission is not checked here: the services this calls ask
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
    // What each action is called in a refusal's `command`. A job-control verb is the last segment of
    // its own route, which is built from the same constant, so the field and the URL cannot drift
    // apart; a send and a browse have no verb in their routes and are named for what they do.
    private const string SendAct = "send";
    private const string BrowseAct = "browse";
    private const string PauseAct = "pause";
    private const string ResumeAct = "resume";
    private const string StopAct = "stop";
    private const string ReadyAct = "ready";
    private const string UnreadyAct = "unready";
    private const string IdleAct = "idle";
    private const string LightingAct = "lighting";

    private readonly PrintFileCatalog _files;
    private readonly TransferService _transfers;
    private readonly PrinterCommandService _commands;
    private readonly PrintStopService _stops;
    private readonly PrinterLightingService _lighting;
    private readonly PrinterQueryService _printers;
    private readonly PrinterAccessService _access;
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<PrinterController> _logger;

    public PrinterController(PrintFileCatalog files,
                             TransferService transfers,
                             PrinterCommandService commands,
                             PrintStopService stops,
                             PrinterLightingService lighting,
                             PrinterQueryService printers,
                             PrinterAccessService access,
                             UserManager<HSUser> userManager,
                             ILogger<PrinterController> logger)
    {
        _files = files;
        _transfers = transfers;
        _commands = commands;
        _stops = stops;
        _lighting = lighting;
        _printers = printers;
        _access = access;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Sends one of the caller's files to a printer. <c>POST /api/v1/printers/{uuid}/files</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sends, and never starts.</b> Starting a print is the queue's alone
    /// (<c>POST printers/{uuid}/queue</c>), because it waits for a person to ready the printer - so
    /// there is no <c>printNow</c> here. There is no <c>teamId</c> in the body either: the team is a
    /// property of the printer and is read from it.
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
    /// <para>
    /// <b>An older version of the file already on that drive is deleted first</b> - one Homespool sent
    /// before the file was overwritten - since the printer refuses a transfer onto a name it holds.
    /// While the printer is using that copy it keeps it, and this answers <c>409</c> with its words.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("printers/{uuid:guid}/files")]
    public async Task<Results<NoContent, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem>> SendFile(
        Guid uuid,
        [FromBody] SendFileRequest body,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, CallerResolver.For(user, User), cancellationToken);

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        // Print is asked before the file is looked up. The lookup is not gated on viewing files, and
        // "you have no file named" is a different answer from whatever comes after it - so asked any
        // later, a caller who may not print could still tell which names exist. A scope refusal names
        // the capability through the exception filter; the team's refusal is answered here.
        try
        {
            await _access.RequireAsync(printer.Id, CallerResolver.For(user, User), Capability.Print, cancellationToken);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }

        // Scoped to the caller, so "someone else's file" and "no such file" are the same answer and
        // neither confirms the other's existence. This is the ownership check, and it is structural.
        StoredFile? file = _files.FindForPrinting(user.Id, body.Name);

        if (file is null)
        {
            return this.NotFoundProblem($"You have no file named {body.Name}.");
        }

        if (file.Length >= uint.MaxValue)
        {
            // orig_size is uint32 on the wire; a file this large cannot be described at all.
            return this.BadRequestProblem("Files must be under 4 GiB - a printer cannot be sent anything larger.");
        }

        PrintFile? indexed = await _files.ResolveAsync(user.Id, file.FileName, cancellationToken);

        if (indexed is null)
        {
            return this.NotFoundProblem($"You have no file named {body.Name}.");
        }

        // Naming the file on the drive, clearing an older version of it there, offering the bytes and
        // recording the attempt all live in TransferService, because the Files page and the queue need
        // exactly the same steps in the same order.
        try
        {
            DirectSendResult result = await _transfers.SendDirectAsync(printer, indexed, file, CallerResolver.For(user, User),
                                                                       cancellationToken);

            if (result.Sent is not { } sent)
            {
                // Said as the printer refusing the send, which is what it amounts to: the newer
                // version cannot go where the older one still is.
                return this.CommandRefused(SendAct,
                                           $"An older version of {file.FileName} is on the printer and could not be " +
                                           $"replaced: {result.Cleared.Reason}");
            }

            CommandOutcome? outcome = sent.Outcome;

            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                // The sender chooses between an inline transfer and an encrypted download from a
                // property of the connection, which the caller cannot see and cannot act on - so the
                // answer names the act, and this line is where the command actually sent is kept.
                _logger.LogInformation("{Command} to printer {PrinterId} answered {Outcome}",
                                       sent.WireName, printer.Id, outcome.EventType.ToString());

                return this.CommandRefused(SendAct, outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString());
            }

            return TypedResults.NoContent();
        }
        catch (PrintFileUnreadableException e)
        {
            return this.ConflictProblem(e.Message);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogInformation(e, "Sending a file to printer {PrinterId} did not complete", printer.Id);

            return this.CommandRefused(SendAct, e.Message);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>
    /// Lists what is on the printer's own storage.
    /// <c>POST /api/v1/printers/{uuid}/storage/usb/{path}</c>, with an empty path meaning the root
    /// and no body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A POST, although the caller only learns something.</b> Nothing is kept here to read: the
    /// listing is obtained by sending the printer a command, which occupies its one command slot and
    /// makes it walk a directory. A GET promises a request that is safe to repeat, prefetch or fire
    /// from a link on another site, and this is none of those - and as a POST it falls under
    /// <see cref="SameOriginWriteFilter"/> like every other request that reaches a
    /// printer.
    /// </para>
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
    /// <b>Gated here, on <see cref="Capability.ControlPrinter"/>, rather than left to the command.</b>
    /// It makes the printer go and work, and <see cref="PrusaConnect.Commands.SendFileInfo"/> itself
    /// only requires <see cref="Capability.ViewPrinter"/>, because the queue loop asks the same
    /// question on behalf of whoever queued a print. So the endpoint is the thing that has to be
    /// stricter, and says so.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("printers/{uuid:guid}/storage/usb/{**path}")]
    public async Task<Results<Ok<PrinterStorageReadDTO>, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem, BadGatewayProblem>>
        Storage(Guid uuid, string? path, CancellationToken cancellationToken)
    {
        // Both separators, because the printer's filesystem honours both: firmware refuses only a
        // "/../" and walks "..\" to wherever it leads on the drive. Kestrel decodes %5C and removes
        // dot segments only between slashes, so "..\" arrives here from any ordinary request.
        if (path is not null && path.Split('/', '\\').Contains(".."))
        {
            return this.BadRequestProblem("Path must contain no '..' segment.");
        }

        (HSUser? user, Printer? printer) = await ResolveAsync(uuid, cancellationToken);

        if (user is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        string trimmed = path?.Trim('/') ?? string.Empty;
        PrusaConnect.Commands.SendFileInfo command = new() { Path = trimmed.Length == 0 ? "/usb" : $"/usb/{trimmed}" };

        // The throwing gate rather than the bool one, so a scope refusal names ControlPrinter through
        // the exception filter as every other scope refusal does. The team's refusal is answered here
        // and names no capability, because no token would fix it.
        try
        {
            await _access.RequireAsync(printer.Id, CallerResolver.For(user, User), Capability.ControlPrinter, cancellationToken);
        }
        catch (TeamAccessDeniedException)
        {
            return this.ForbiddenProblem("You may not browse this printer's storage.");
        }

        try
        {
            CommandOutcome<FileInfoEventDataDTO>? outcome =
                await _commands.AskAsync(printer.Id, command, CallerResolver.For(user, User), cancellationToken);

            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                // Firmware answers a path that does not exist and a path it will not touch with the
                // same event, distinguished only by reason text - so this stays one status code and
                // hands the caller firmware's own words rather than guessing at a 404.
                return this.CommandRefused(BrowseAct,
                                           outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString());
            }

            if (outcome?.Answer is null)
            {
                // Answered, and with nothing in it. Not a refusal and not unreadable - there is
                // simply no listing to return, and inventing an empty one would claim the storage is
                // empty when what happened is that the printer said nothing.
                _logger.LogInformation("{Command} to printer {PrinterId} answered {Outcome} with no data",
                                       command.WireName, printer.Id, outcome?.EventType.ToString() ?? "nothing");

                return this.CommandAnswerUnusable(BrowseAct, "The printer answered without a listing.");
            }

            return TypedResults.Ok(PrinterStorageReadDTO.FromEvent(outcome.Answer));
        }
        catch (CommandAnswerUnreadableException e)
        {
            // The printer answered and we could not read it, which is the gateway's failure and not
            // the caller's - so 502 rather than the 409 the transport failures below get.
            _logger.LogWarning(e, "{Command} to printer {PrinterId} answered unreadably", command.WireName, printer.Id);

            return this.CommandAnswerUnusable(BrowseAct, e.Message);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogInformation(e, "{Command} to printer {PrinterId} did not complete", command.WireName, printer.Id);

            return this.CommandRefused(BrowseAct, e.Message);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
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
    /// What is new here is the route, not the command path.
    /// </para>
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/" + PauseAct)]
    public Task<JobControlResult> Pause(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, PauseAct, new PausePrint(), cancellationToken);
    }

    /// <summary>Resumes a paused print. <c>PUT /api/v1/printers/{uuid}/command/resume</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/" + ResumeAct)]
    public Task<JobControlResult> Resume(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, ResumeAct, new ResumePrint(), cancellationToken);
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
    [Route("printers/{uuid:guid}/command/" + StopAct)]
    public async Task<JobControlResult> Stop(Guid uuid, CancellationToken cancellationToken)
    {
        // Which printer, and whether this caller may be told it exists.
        (HSUser? user, Printer? printer) = await ResolveAsync(uuid, cancellationToken);

        if (user is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        // The one difference from the other five verbs: PrintStopService sends the same command
        // through the same permission gate, and notes who asked on the way past.
        return await SendAsync(printer, StopAct, new StopPrint(), cancellationToken, send: _stops.StopAsync);
    }

    /// <summary>
    /// Marks the printer ready for a queued job. <c>PUT /api/v1/printers/{uuid}/command/ready</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered <c>StateChanged</c> rather than <c>Finished</c> on hardware - both are success as far
    /// as this endpoint is concerned, since only Rejected and Failed are refusals.
    /// </para>
    /// <para>
    /// <b>Switched off (2026-08-30), and left whole rather than deleted.</b> Readying is a person's
    /// assertion that the print sheet is clear, and this is the one route to it with no person on the
    /// other end. <see cref="NonActionAttribute"/> rather than a body that refuses: the route then
    /// does not exist at all - no entry in the OpenAPI document, nothing advertised that answers only
    /// to say no - while the method, its route template and its wiring stay exactly as they were.
    /// </para>
    /// <para>
    /// <b>To put it back, delete the one attribute below.</b> It returns gated rather than open:
    /// <see cref="SetPrinterReady"/> declares
    /// <see cref="Printing.IPrinterIntent.RequiresRemoteReadyAllowed"/>, so the printer's own toggle
    /// is enforced beneath this whatever route reaches it - which is what makes restoring it a
    /// one-line decision instead of a re-audit.
    /// </para>
    /// </remarks>
    [NonAction]
    [HttpPut]
    [Route("printers/{uuid:guid}/command/" + ReadyAct)]
    public Task<JobControlResult> Ready(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, ReadyAct, new SetPrinterReady(), cancellationToken);
    }

    /// <summary>Cancels the ready state. <c>PUT /api/v1/printers/{uuid}/command/unready</c>.</summary>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/" + UnreadyAct)]
    public Task<JobControlResult> Unready(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, UnreadyAct, new CancelPrinterReady(), cancellationToken);
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
    [Route("printers/{uuid:guid}/command/" + IdleAct)]
    public Task<JobControlResult> Idle(Guid uuid, CancellationToken cancellationToken)
    {
        return SendJobControlAsync(uuid, IdleAct, new SetPrinterIdle(), cancellationToken);
    }

    /// <summary>
    /// Sets how bright the printer's own lighting is.
    /// <c>PUT /api/v1/printers/{uuid}/command/lighting</c>, with <c>{"intensity": 0-100}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ours rather than Connect's, like <c>idle</c></b>, and shaped like its neighbours: 204 once the
    /// printer has taken it, 409 when it will not or cannot. It is the one verb here with a body,
    /// because it is the one that sets a value rather than moving a print along.
    /// </para>
    /// <para>
    /// <b>The range is checked here</b>, because firmware does not: it stores the value in a byte and
    /// wraps, so 101 would come out nearly off. A printer with no lighting is a 409 before anything is
    /// sent - see <see cref="PrinterLighting"/>.
    /// </para>
    /// <para>
    /// The brightness reads back as <c>lighting</c> on <c>GET printers/{uuid}/telemetry</c>, not
    /// always as the number sent: firmware's round trip through a byte turns 33 into 32.
    /// </para>
    /// </remarks>
    [HttpPut]
    [Route("printers/{uuid:guid}/command/" + LightingAct)]
    public async Task<Results<NoContent, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem>> Lighting(
        Guid uuid,
        [FromBody] SetLightingRequest body,
        CancellationToken cancellationToken)
    {
        (HSUser? user, Printer? printer) = await ResolveAsync(uuid, cancellationToken);

        if (user is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        try
        {
            await _lighting.SetAsync(printer.Id, CallerResolver.For(user, User), body.Intensity, cancellationToken);

            return TypedResults.NoContent();
        }
        catch (LightingIntensityOutOfRangeException e)
        {
            return this.BadRequestProblem(e.Message);
        }
        catch (NoLightingException e)
        {
            return this.ConflictProblem(e.Message);
        }
        catch (PrinterRefusedException e)
        {
            return this.CommandRefused(LightingAct, e.Reason ?? "The printer refused the command.", e.EventType.ToString());
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogInformation(e, "Setting the lighting of printer {PrinterId} did not complete", printer.Id);

            return this.CommandRefused(LightingAct, e.Message);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>Resolves the printer, then sends - the whole body of every job-control verb above.</summary>
    private async Task<JobControlResult> SendJobControlAsync(Guid uuid,
                                                             string act,
                                                             IPrinterIntent command,
                                                             CancellationToken cancellationToken)
    {
        (HSUser? user, Printer? printer) = await ResolveAsync(uuid, cancellationToken);

        if (user is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        return await SendAsync(printer, act, command, cancellationToken);
    }

    /// <summary>
    /// The caller's account and the printer the route names - either null being a refusal the action
    /// answers itself, in that order.
    /// </summary>
    /// <remarks>
    /// A null printer covers both "no such printer" and "not visible to this user", deliberately -
    /// telling them apart would confirm the existence of other people's printers.
    /// </remarks>
    private async Task<(HSUser? user, Printer? printer)> ResolveAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return (null, null);
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, CallerResolver.For(user, User), cancellationToken);

        return (user, printer);
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
    private async Task<JobControlResult> SendAsync(Printer printer,
                                                   string act,
                                                   IPrinterIntent command,
                                                   CancellationToken cancellationToken,
                                                   Action? onFailure = null,
                                                   Func<int, Caller, CancellationToken, Task<CommandOutcome?>>? send = null)
    {
        HSUser user = (await _userManager.GetUserAsync(User))!;
        Caller caller = CallerResolver.For(user, User);

        try
        {
            CommandOutcome? outcome;

            if (send is null)
            {
                outcome = await _commands.SendCommandAsync(printer.Id, command, caller, cancellationToken);
            }
            else
            {
                outcome = await send(printer.Id, caller, cancellationToken);
            }

            // A null outcome is a command the printer cannot answer, written successfully - nothing
            // to inspect, and 204 is the honest result. None of this controller's commands are of
            // that kind today.
            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                onFailure?.Invoke();

                return this.CommandRefused(act, outcome.Reason ?? "The printer refused the command.", outcome.EventType.ToString());
            }

            // 204, which is ours rather than the spec's - Connect documents 200 with a Command
            // resource for these. Answering the printer's real verdict is more useful to a caller
            // than an acknowledgement that we asked. The printer's actual reply is logged and
            // persisted as an ordinary event either way; a caller wanting it watches the event stream.
            return TypedResults.NoContent();
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            onFailure?.Invoke();
            _logger.LogInformation(e, "{Command} to printer {PrinterId} did not complete", command.Name, printer.Id);

            return this.CommandRefused(act, e.Message);
        }

        // Both are "no, and no permission you could be granted changes that" - one because the caller
        // may not use this printer, one because the printer may not be readied remotely at all. The
        // second is unreachable while Ready is [NonAction] and is here so that undoing that cannot
        // turn a refusal into a 500.
        catch (Exception e) when (e is TeamAccessDeniedException or RemoteReadyNotAllowedException)
        {
            onFailure?.Invoke();

            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>Body of a lighting change.</summary>
    public class SetLightingRequest
    {
        /// <summary>Brightness in percent, 0 to 100. Zero is off.</summary>
        public required int Intensity { get; set; }
    }

    /// <summary>Body of a send: which of the caller's files to transfer.</summary>
    public class SendFileRequest
    {
        /// <summary>The file's name, as the file API lists it.</summary>
        public required string Name { get; set; }
    }
}
