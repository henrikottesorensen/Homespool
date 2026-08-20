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

using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
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
    private readonly QueueSnapshotReader _snapshots;
    private readonly PrinterQueryService _printers;
    private readonly ErrorText _errors;
    private readonly UserManager<HSUser> _userManager;

    public PrintQueueController(PrintQueueService queue,
                                QueueSnapshotReader snapshots,
                                PrinterQueryService printers,
                                ErrorText errors,
                                UserManager<HSUser> userManager)
    {
        _queue = queue;
        _snapshots = snapshots;
        _printers = printers;
        _errors = errors;
        _userManager = userManager;
    }

    /// <summary>
    /// What this printer will print, in order. <c>GET /api/v1/printers/{uuid}/queue</c>.
    /// </summary>
    [HttpGet]
    [Route("printers/{uuid:guid}/queue")]
    public async Task<Results<Ok<PrintQueueReadDTO>, ForbiddenProblem, NotFoundProblem>> List(Guid uuid,
                                                                                            CancellationToken cancellationToken)
    {
        (Printer? printer, Caller? caller) = await ResolveAsync(uuid, cancellationToken);

        if (caller is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        try
        {
            IReadOnlyList<QueuedPrint> jobs = await _queue.ListAsync(printer.Id, caller, cancellationToken);

            // The same snapshot the loop reads, through the same rules - so a client is told what the
            // loop actually believes rather than a second opinion computed here.
            QueueSnapshot snapshot = await _snapshots.ReadAsync(printer.Id, cancellationToken);

            return TypedResults.Ok(new PrintQueueReadDTO
            {
                // Said here rather than returned as a key, because the DTO documents this field as
                // "a sentence for a person" and a caller that had to resolve keys would need our
                // resource file. It follows Accept-Language like every other response, so a script
                // that sends none keeps getting exactly the English it got before.
                Waiting = Waiting(snapshot),
                Prints = jobs.Select(QueuedPrintReadDTO.FromQueuedPrint).ToList(),
            });
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
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
    public async Task<Results<Created<QueuedPrintReadDTO>, ForbiddenProblem, NotFoundProblem>> Enqueue(
        Guid uuid,
        [FromBody] EnqueueRequest body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        (Printer? printer, Caller? caller) = await ResolveAsync(uuid, cancellationToken);

        if (caller is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        try
        {
            EnqueueOutcome outcome = await _queue.EnqueueAsync(printer.Id, caller, body.Name, cancellationToken);

            // Re-read so the response carries the file's name and size, which the entity does not hold.
            IReadOnlyList<QueuedPrint> queue = await _queue.ListAsync(printer.Id, caller, cancellationToken);
            QueuedPrintReadDTO created = QueuedPrintReadDTO.FromQueuedPrint(queue.Single(entry => entry.Id == outcome.Queued.Id));

            // Location is the queue the entry now sits in - the same URL CreatedAtAction would have
            // composed, built through the same helper.
            return TypedResults.Created(Url.Action(nameof(List), new { uuid }), created);
        }
        catch (PrintFileNotFoundException e)
        {
            return this.NotFoundProblem(e.Message);
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>
    /// Moves a queued print to a new position, counting from zero. An index past either end is clamped.
    /// <c>PATCH /api/v1/printers/{uuid}/queue/{trackingId}</c>.
    /// </summary>
    [HttpPatch]
    [Route("printers/{uuid:guid}/queue/{trackingId:guid}")]
    public async Task<Results<NoContent, ForbiddenProblem, NotFoundProblem>> Move(Guid uuid,
                                                                                 Guid trackingId,
                                                                                 [FromBody] MoveRequest body,
                                                                                 CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        (Printer? printer, Caller? caller) = await ResolveAsync(uuid, cancellationToken);

        if (caller is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        try
        {
            bool found = await _queue.MoveAsync(trackingId, caller, body.Position, cancellationToken);

            if (!found)
            {
                return this.NotFoundProblem();
            }

            return TypedResults.NoContent();
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>
    /// Cancels a queued print. <c>DELETE /api/v1/printers/{uuid}/queue/{trackingId}</c>.
    /// </summary>
    /// <remarks>
    /// <b>This never stops a print that has already started.</b> Once the loop has taken an entry it
    /// is a print, not a plan, and ending one is a deliberate separate act - "don't cancel prints on
    /// people" (Henrik, <c>notes/print-queue.md</c>).
    /// </remarks>
    [HttpDelete]
    [Route("printers/{uuid:guid}/queue/{trackingId:guid}")]
    public async Task<Results<NoContent, ForbiddenProblem, NotFoundProblem>> Cancel(Guid uuid,
                                                                                   Guid trackingId,
                                                                                   CancellationToken cancellationToken)
    {
        (Printer? printer, Caller? caller) = await ResolveAsync(uuid, cancellationToken);

        if (caller is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        try
        {
            bool found = await _queue.CancelAsync(trackingId, caller, cancellationToken);

            if (!found)
            {
                return this.NotFoundProblem();
            }

            return TypedResults.NoContent();
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>
    /// Resolves the route's printer and the caller - either null being a refusal the action answers
    /// itself, the caller first.
    /// </summary>
    /// <remarks>
    /// A null printer covers both "no such printer" and "not visible to this user", deliberately -
    /// telling them apart would confirm the existence of other people's printers. The same rule
    /// <see cref="PrinterController"/> follows.
    /// </remarks>
    private async Task<(Printer? printer, Caller? caller)> ResolveAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return (null, null);
        }

        Caller caller = CallerResolver.For(user, User);
        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, caller, cancellationToken);

        return (printer, caller);
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

    /// <summary>The sentence for what a queue is waiting on, or null when it needs no explanation.</summary>
    private string? Waiting(QueueSnapshot snapshot)
    {
        MessageKey? waiting = QueueWaitDescription.For(QueueRules.Decide(snapshot), snapshot.Head?.FileName);

        return waiting is null ? null : _errors.For(waiting);
    }
}
