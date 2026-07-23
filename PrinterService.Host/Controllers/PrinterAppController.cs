using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO.App;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Controllers;

/// <summary>
/// The app-facing surface phase-1.5 emulates from Connect's mobile API (AGENT-NOTES phase-1.5 §15).
/// Cookie-authenticated, unlike <see cref="PrusaConnectPrinterController"/>'s printer-facing
/// endpoints - exercisable with curl or a browser, not by the real Prusa app, which expects a
/// bearer JWT. <c>[ApiController]</c> is used deliberately here, unlike on the printer-facing
/// controller: this is a first-party surface we control, not a firmware-dictated contract.
/// </summary>
/// <remarks>
/// <c>GET /api/v1/init</c> is deliberately not implemented - its spec schema
/// (<c>createdAt/updatedAt/finishedAt/failedAt</c>) doesn't correspond to anything in our model,
/// and what it's even for isn't clear from the spec alone (Henrik, phase-1.5 §15 step 7b).
/// </remarks>
[Authorize]
[ApiController]
[Route("/api/v1")]
public class PrinterAppController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly PrinterQueryService _printerQueryService;
    private readonly TeamService _teamService;
    private readonly UserManager<PSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<PrinterAppController> _logger;

    public PrinterAppController(PrusaConnectService prusaConnectService,
                                PrinterQueryService printerQueryService,
                                TeamService teamService,
                                UserManager<PSUser> userManager,
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
    public async Task<ActionResult<PrinterReadDTO>> RegisterPrinter([FromBody] RegisterPrinterAppRequestDTO body, CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than claim on an invented id.
            return Forbid();
        }

        // ClaimPrinterAsync already saves atomically on its own, but the transaction gives step 7b
        // (and anything else added around the claim later) a safe container to share without needing
        // to know about it - the same reasoning as Setup.cshtml.cs and Register.cshtml.cs. Any early
        // return before CommitAsync disposes the transaction uncommitted, rolling back every write
        // made through it.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            Printer printer = await _prusaConnectService.ClaimPrinterAsync(
                body.Code, body.Name, body.Location, body.TeamId, user.Id);

            await transaction.CommitAsync(cancellationToken);

            return StatusCode(StatusCodes.Status201Created, PrinterReadDTO.FromEntity(printer));
        }
        catch (PrinterNotFoundException)
        {
            return NotFound();
        }
        catch (RegistrationAlreadyClaimedException)
        {
            return Conflict();
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to claim printer for registration code; rolling back.");

            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet]
    [Route("user")]
    public async Task<ActionResult<UserReadDTO>> GetCurrentUser(CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);

        return Ok(UserReadDTO.FromEntity(user, memberships));
    }

    [HttpGet]
    [Route("printers")]
    public async Task<ActionResult<IReadOnlyList<PrinterReadDTO>>> ListPrinters(CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        IReadOnlyList<Printer> printers = await _printerQueryService.ListPrintersForUserAsync(user.Id, cancellationToken);

        return Ok(printers.Select(PrinterReadDTO.FromEntity).ToList());
    }

    [HttpGet]
    [Route("printers/{uuid:guid}")]
    public async Task<ActionResult<PrinterReadDTO>> GetPrinter(Guid uuid, CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        Printer? printer = await _printerQueryService.GetPrinterForUserAsync(uuid, user.Id, cancellationToken);

        if (printer is null)
        {
            return NotFound();
        }

        return Ok(PrinterReadDTO.FromEntity(printer));
    }

    [HttpPatch]
    [Route("printers/{uuid:guid}")]
    public async Task<ActionResult<PrinterReadDTO>> PatchPrinter(Guid uuid, [FromBody] PrinterPatchInputDTO body, CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            Printer? printer = await _printerQueryService.UpdatePrinterAsync(uuid, user.Id, body.Name, body.Location, cancellationToken);

            if (printer is null)
            {
                return NotFound();
            }

            await transaction.CommitAsync(cancellationToken);

            return Ok(PrinterReadDTO.FromEntity(printer));
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to update printer {PrinterUuid}; rolling back.", uuid);

            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
