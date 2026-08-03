using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// A printer's queue over the app API: what is waiting, and the three things that change it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own controller rather than more routes on <see cref="PrinterController"/></b>, which owns
/// things done <i>to</i> a printer - each of which sends the hardware a command and waits for its
/// answer. Nothing here touches a printer at all: the queue is a list, and the producer loop is what
/// turns an entry into a command later. Two different failure modes, so two controllers.
/// </para>
/// <para>
/// <b>Not Connect-shaped</b> (<c>notes/file-storage.md</c>, "The API is ours"). Prusa's spec spells
/// this <c>planned_jobs</c>; that is their vocabulary and this is not their API.
/// </para>
/// <para>
/// Permission lives in <see cref="PrintQueueService"/> - reading needs <c>CanRead</c>, changing needs
/// <c>CanUse</c> - so it is decided in one place rather than restated per action here.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrintQueueController : ControllerBase
{
    private readonly PrintQueueService _queue;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;

    public PrintQueueController(PrintQueueService queue, PrinterQueryService printers,
        UserManager<HSUser> userManager)
    {
        _queue = queue;
        _printers = printers;
        _userManager = userManager;
    }

    /// <summary>
    /// What this printer will print, in order. <c>GET /api/v1/printers/{uuid}/queue</c>.
    /// </summary>
    [HttpGet]
    [Route("printers/{uuid:guid}/queue")]
    [ProducesResponseType<IReadOnlyList<QueuedPrintReadDTO>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<QueuedPrintReadDTO>>> List(Guid uuid,
                                                                          CancellationToken cancellationToken)
    {
        (Printer? printer, long userId, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        try
        {
            IReadOnlyList<QueuedPrint> jobs = await _queue.ListAsync(printer.Id, userId, cancellationToken);

            return Ok(jobs.Select(QueuedPrintReadDTO.FromQueuedPrint).ToList());
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Adds one of the caller's files to the end of the queue.
    /// <c>POST /api/v1/printers/{uuid}/queue</c>.
    /// </summary>
    /// <remarks>
    /// <b>Queueing is not sending.</b> Nothing is transferred here and no command reaches the printer -
    /// the entry is a row, and the printer's producer loop decides when to move the bytes and when to
    /// start the print. <c>POST printers/{uuid}/files</c> is the call that sends a file right now.
    /// </remarks>
    [HttpPost]
    [Route("printers/{uuid:guid}/queue")]
    [ProducesResponseType<QueuedPrintReadDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueuedPrintReadDTO>> Enqueue(Guid uuid,
                                                              [FromBody] EnqueueRequest body,
                                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        (Printer? printer, long userId, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        try
        {
            QueuedPrint job = await _queue.EnqueueAsync(printer.Id, userId, body.Name, cancellationToken);

            // Re-read so the response carries the file's name and size, which the entity does not hold.
            IReadOnlyList<QueuedPrint> queue = await _queue.ListAsync(printer.Id, userId, cancellationToken);
            QueuedPrintReadDTO created = QueuedPrintReadDTO.FromQueuedPrint(queue.Single(entry => entry.Id == job.Id));

            return CreatedAtAction(nameof(List), new { uuid }, created);
        }
        catch (PrintFileNotFoundException e)
        {
            return this.Failure(StatusCodes.Status404NotFound, e.Message);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Moves a queued print to a new position, counting from zero. An index past either end is clamped.
    /// <c>PATCH /api/v1/printers/{uuid}/queue/{queuedPrintId}</c>.
    /// </summary>
    [HttpPatch]
    [Route("printers/{uuid:guid}/queue/{queuedPrintId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Move(Guid uuid,
                                         long queuedPrintId,
                                         [FromBody] MoveRequest body,
                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        (Printer? printer, long userId, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        try
        {
            return await _queue.MoveAsync(queuedPrintId, userId, body.Position, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Cancels a queued print. <c>DELETE /api/v1/printers/{uuid}/queue/{queuedPrintId}</c>.
    /// </summary>
    /// <remarks>
    /// <b>This never stops a print that has already started.</b> Once the loop has taken an entry it
    /// is a print, not a plan, and ending one is a deliberate separate act - "don't cancel prints on
    /// people" (Henrik, <c>notes/print-queue.md</c>).
    /// </remarks>
    [HttpDelete]
    [Route("printers/{uuid:guid}/queue/{queuedPrintId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Cancel(Guid uuid, long queuedPrintId, CancellationToken cancellationToken)
    {
        (Printer? printer, long userId, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        try
        {
            return await _queue.CancelAsync(queuedPrintId, userId, cancellationToken) ? NoContent() : NotFound();
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Resolves the route's printer and the caller, or the failure to answer with.
    /// </summary>
    /// <remarks>
    /// Null covers both "no such printer" and "not visible to this user", deliberately - telling them
    /// apart would confirm the existence of other people's printers. The same rule
    /// <see cref="PrinterController"/> follows.
    /// </remarks>
    private async Task<(Printer? printer, long userId, ActionResult? failure)> ResolveAsync(Guid uuid,
                                                                                            CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return (null, 0, Forbid());
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, user.Id, cancellationToken);

        return printer is null ? (null, user.Id, NotFound()) : (printer, user.Id, null);
    }

    /// <summary>Body of an enqueue: which of the caller's files to add.</summary>
    public class EnqueueRequest
    {
        /// <summary>
        /// The file's name, as the caller's own file listing reports it. Resolved against their files
        /// alone, so another user's name finds nothing.
        /// </summary>
        public required string Name { get; set; }
    }

    /// <summary>Body of a move: where the entry should end up.</summary>
    public class MoveRequest
    {
        /// <summary>Target index, counting from zero. Past either end is clamped rather than refused.</summary>
        public required int Position { get; set; }
    }
}
