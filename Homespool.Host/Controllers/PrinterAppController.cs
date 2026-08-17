using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// The app-facing surface phase-1.5 emulates from Connect's mobile API (AGENT-NOTES phase-1.5 §15).
/// Authenticated by sign-in cookie <b>or</b> personal access token, unlike
/// <see cref="PrusaConnectPrinterController"/>'s printer-facing endpoints - exercisable with curl or a
/// browser, not by the real Prusa app, which expects a bearer JWT of its own shape.
/// <c>[ApiController]</c> is used deliberately here, unlike on the printer-facing controller: this is
/// a first-party surface we control, not a firmware-dictated contract.
/// </summary>
/// <remarks>
/// <c>GET /api/v1/init</c> is deliberately not implemented - its spec schema
/// (<c>createdAt/updatedAt/finishedAt/failedAt</c>) doesn't correspond to anything in our model,
/// and what it's even for isn't clear from the spec alone (Henrik, phase-1.5 §15 step 7b).
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]
[ProducesResponseType<ProblemDetails>(StatusCodes
    .Status401Unauthorized)] // 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
public class PrinterAppController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly PrinterQueryService _printerQueryService;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<PrinterAppController> _logger;

    public PrinterAppController(PrusaConnectService prusaConnectService,
                                PrinterQueryService printerQueryService,
                                TeamService teamService,
                                UserManager<HSUser> userManager,
                                UnitOfWork unitOfWork,
                                ILogger<PrinterAppController> logger)
    {
        _prusaConnectService = prusaConnectService;
        _printerQueryService = printerQueryService;
        _teamService = teamService;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    [HttpPost]
    [Route("printers/register")]
    public async Task<Results<Created<PrinterReadDTO>, ForbiddenProblem, NotFoundProblem, ConflictProblem, InternalServerErrorProblem>>
        RegisterPrinter([FromBody] RegisterPrinterAppRequestDTO body, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        // The transaction is required, not a convenience. ClaimPrinterAsync makes three separate
        // SaveChangesAsync calls, so without one an interrupted claim can leave a printer half
        // claimed - and this comment used to say the opposite ("already saves atomically on its
        // own"), which would have told whoever wrote the next caller that they needed nothing.
        // Pages/Printers/Claim.cshtml.cs is the other caller and wraps it for the same reason.
        // Any early return before CommitAsync disposes the transaction uncommitted, rolling back
        // every write made through it.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            Printer printer = await _prusaConnectService.ClaimPrinterAsync(
                body.Code, body.Name, body.Location, body.TeamId, CallerResolver.For(user, User));

            await transaction.CommitAsync(cancellationToken);

            // Re-read rather than mapping the claimed entity directly, so the response carries the
            // permission flags and describes the same resource the next GET will. Mapping it bare
            // would report canRead/canUse/canManage as false to the person who just claimed it.
            PrinterWithState? claimed = await _printerQueryService.GetPrinterWithStateForUserAsync(
                printer.Uuid, CallerResolver.For(user, User), cancellationToken);

            // 201 with no Location: the printer is at GET printers/{uuid}, and the body carries the
            // uuid, but this surface has never advertised the header and a claim is not quite a create.
            return TypedResults.Created((string?)null,
                                        claimed is null ? PrinterReadDTO.FromEntity(printer) : PrinterReadDTO.FromEntity(claimed));
        }
        catch (PrinterNotFoundException e)
        {
            return this.NotFoundProblem(e.Message);
        }
        catch (RegistrationAlreadyClaimedException e)
        {
            return this.ConflictProblem(e.Message);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to claim printer for registration code; rolling back.");

            return this.InternalServerErrorProblem("The claim could not be saved.");
        }
    }

    [HttpGet]
    [Route("user")]
    public async Task<Results<Ok<UserReadDTO>, ForbiddenProblem>> GetCurrentUser(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);

        return TypedResults.Ok(UserReadDTO.FromEntity(user, memberships));
    }

    [HttpGet]
    [Route("printers")]
    public async Task<Results<Ok<IReadOnlyList<PrinterReadDTO>>, ForbiddenProblem>> ListPrinters(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        IReadOnlyList<PrinterWithState> printers =
            await _printerQueryService.ListPrintersWithStateForUserAsync(CallerResolver.For(user, User), cancellationToken);

        return TypedResults.Ok<IReadOnlyList<PrinterReadDTO>>(printers.Select(PrinterReadDTO.FromEntity).ToList());
    }

    [HttpGet]
    [Route("printers/{uuid:guid}")]
    public async Task<Results<Ok<PrinterReadDTO>, ForbiddenProblem, NotFoundProblem>> GetPrinter(Guid uuid,
                                                                                               CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        PrinterWithState? printer = await _printerQueryService.GetPrinterWithStateForUserAsync(uuid, CallerResolver.For(user, User), cancellationToken);

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        return TypedResults.Ok(PrinterReadDTO.FromEntity(printer));
    }

    [HttpPatch]
    [Route("printers/{uuid:guid}")]
    public async Task<Results<Ok<PrinterReadDTO>, ForbiddenProblem, NotFoundProblem, InternalServerErrorProblem>> PatchPrinter(
        Guid uuid,
        [FromBody] PrinterPatchInputDTO body,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            PrinterWithState? printer =
                await _printerQueryService.UpdatePrinterAsync(uuid, CallerResolver.For(user, User), body.Name, body.Location, cancellationToken);

            if (printer is null)
            {
                return this.NotFoundProblem();
            }

            await transaction.CommitAsync(cancellationToken);

            return TypedResults.Ok(PrinterReadDTO.FromEntity(printer));
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to update printer {PrinterUuid}; rolling back.", uuid);

            return this.InternalServerErrorProblem("The change could not be saved.");
        }
    }
}
