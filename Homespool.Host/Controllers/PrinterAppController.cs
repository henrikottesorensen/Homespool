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

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// The app-facing surface emulated from Connect's mobile API.
/// Authenticated by sign-in cookie <b>or</b> personal access token, unlike
/// <see cref="PrusaConnectPrinterController"/>'s printer-facing endpoints - exercisable with curl or a
/// browser, not by the real Prusa app, which expects a bearer JWT of its own shape.
/// A first-party surface we control, so ProblemDetails bodies and automatic model-validation
/// responses are ours to shape here in a way a firmware-dictated contract never is.
/// </summary>
/// <remarks>
/// <c>GET /api/v1/init</c> is deliberately not implemented - its spec schema
/// (<c>createdAt/updatedAt/finishedAt/failedAt</c>) doesn't correspond to anything in our model,
/// and what it's even for isn't clear from the spec alone.
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]
[ProducesResponseType<ProblemDetails>(StatusCodes
    .Status401Unauthorized)] // 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
public class PrinterAppController : ControllerBase
{
    private readonly RegistrationCodeClaim _registrationCodeClaim;
    private readonly PrinterQueryService _printerQueryService;
    private readonly TeamService _teamService;
    private readonly DefaultPrinterService _defaults;
    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<PrinterAppController> _logger;

    public PrinterAppController(RegistrationCodeClaim registrationCodeClaim,
                                PrinterQueryService printerQueryService,
                                TeamService teamService,
                                DefaultPrinterService defaults,
                                UserManager<HSUser> userManager,
                                UnitOfWork unitOfWork,
                                ILogger<PrinterAppController> logger)
    {
        _registrationCodeClaim = registrationCodeClaim;
        _printerQueryService = printerQueryService;
        _teamService = teamService;
        _defaults = defaults;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    [HttpPost]
    [Route("printers/register")]

    // The body is a name, a location and a code. [StringLength] refuses an over-long field, but only
    // after the whole request has been buffered and deserialised, so the attributes bound what is
    // stored and this bounds what is read - Kestrel's thirty-odd megabytes is otherwise the only
    // ceiling. Authenticated, unlike /p/register's cap, which makes it a smaller worry and the same fix.
    [RequestSizeLimit(8 * 1024)]
    public async Task<Results<Created<PrinterReadDTO>, ForbiddenProblem, NotFoundProblem, ConflictProblem, TooManyRequestsProblem, InternalServerErrorProblem>>
        RegisterPrinter([FromBody] RegisterPrinterAppRequestDTO body, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        try
        {
            // RegistrationCodeClaim owns the transaction and the per-account guess count, which the
            // claim page shares - one allowance for both surfaces, not one each.
            Printer printer = await _registrationCodeClaim.ClaimAsync(
                user.Id, body.Code, body.Name, body.Location, body.TeamUuid, CallerResolver.For(user, User), cancellationToken);

            // Re-read rather than mapping the claimed entity directly, so the response carries the
            // caller's capabilities and describes the same resource the next GET will. Mapping it bare
            // would report an empty capability list to the person who just claimed it.
            PrinterWithState? claimed = await _printerQueryService.GetPrinterWithStateForUserAsync(
                printer.Uuid, CallerResolver.For(user, User), cancellationToken);

            // 201 with no Location: the printer is at GET printers/{uuid}, and the body carries the
            // uuid, but this surface has never advertised the header and a claim is not quite a create.
            return TypedResults.Created((string?)null,
                                        claimed is null ? PrinterReadDTO.FromEntity(printer) : PrinterReadDTO.FromEntity(claimed));
        }
        catch (ClaimLockedOutException e)
        {
            return this.TooManyRequestsProblem(e.Message);
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

        Caller caller = CallerResolver.For(user, User);

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);

        Printer? defaultPrinter = await _defaults.ResolvePrinterAsync(user, caller, cancellationToken);

        return TypedResults.Ok(UserReadDTO.FromEntity(user, memberships, defaultPrinter?.Uuid, caller));
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

    // Two short strings, bounded for the same reason as the register action above.
    [RequestSizeLimit(8 * 1024)]
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
