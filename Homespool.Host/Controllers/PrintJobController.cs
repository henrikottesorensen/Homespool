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

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// A printer's prints over the app API: the one running now, the ones that have ended, and printing
/// one of your own again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own controller rather than more routes on <see cref="PrintQueueController"/></b>, which owns
/// what a printer will do. This is what it has done - a record the queue's loop writes and nothing
/// here changes. Reprinting lives here anyway because it starts from a row in this record, not from a
/// file name.
/// </para>
/// <para>
/// Permission lives in the services: reading needs <c>ViewHistory</c>, and reprinting needs that and
/// <c>Print</c>, since it finds a row and then queues.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrintJobController : ControllerBase
{
    /// <summary>How many finished prints a response carries when the caller does not say.</summary>
    public const int DefaultLimit = 20;

    /// <summary>The most a caller may ask for at once. More is clamped rather than refused.</summary>
    public const int MaximumLimit = 100;

    private readonly PrintHistoryService _history;
    private readonly PrintQueueService _queue;
    private readonly PrinterAccessService _access;
    private readonly PrinterQueryService _printers;
    private readonly UserNameLookup _people;
    private readonly ErrorText _errors;
    private readonly UserManager<HSUser> _userManager;

    public PrintJobController(PrintHistoryService history,
                              PrintQueueService queue,
                              PrinterAccessService access,
                              PrinterQueryService printers,
                              UserNameLookup people,
                              ErrorText errors,
                              UserManager<HSUser> userManager)
    {
        _history = history;
        _queue = queue;
        _access = access;
        _printers = printers;
        _people = people;
        _errors = errors;
        _userManager = userManager;
    }

    /// <summary>
    /// The print running on this printer, and its finished prints, newest first.
    /// <c>GET /api/v1/printers/{printerUuid}/jobs</c>.
    /// </summary>
    /// <param name="printerUuid">The printer.</param>
    /// <param name="before">
    /// Only prints that started before this moment - the <c>startedAt</c> of the last print of the
    /// previous page, to read further back.
    /// </param>
    /// <param name="limit">How many finished prints at most. Clamped to between 1 and 100.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpGet]
    [Route("printers/{printerUuid:guid}/jobs")]
    public async Task<Results<Ok<PrintJobsReadDTO>, ForbiddenProblem, NotFoundProblem>> List(
        Guid printerUuid,
        [FromQuery] DateTimeOffset? before,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        (Printer? printer, Caller? caller) = await ResolveAsync(printerUuid, cancellationToken);

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
            int size = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);

            IReadOnlyList<PrintJob> finished = await _history.ListAsync(printer.Id, caller, before, size, cancellationToken);

            // The running print belongs to the first page only: a caller reading further back asked
            // about the past, and repeating the present on every page would put it among prints it
            // did not start before.
            PrintJob? active = before is null ?
                await _history.GetActiveAsync(printer.Id, caller, cancellationToken) :
                null;

            IEnumerable<PrintJob> shown = active is null ? finished : finished.Prepend(active);

            IReadOnlyDictionary<long, UserReference> people = await _people.ReferencesForAsync(
                shown.SelectMany(job => job.StoppedByUserId is { } stopper ?
                                            new[] { job.QueuedByUserId, stopper } :
                                            new[] { job.QueuedByUserId }),
                cancellationToken);

            bool mayQueue = await _access.AllowsAsync(printer.Id, caller, Capability.Print, cancellationToken);

            PrintJobReadDTO Read(PrintJob job)
            {
                return PrintJobReadDTO.FromPrintJob(job, people, mayQueue && job.QueuedByUserId == caller.UserId);
            }

            return TypedResults.Ok(new PrintJobsReadDTO
            {
                Active = active is null ? null : Read(active),
                Prints = [.. finished.Select(Read)],
            });
        }
        catch (TeamAccessDeniedException e)
        {
            return this.ForbiddenProblem(e.Message);
        }
    }

    /// <summary>
    /// Queues one of the caller's own prints again.
    /// <c>POST /api/v1/printers/{printerUuid}/jobs/{printUuid}/reprint</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the person who queued a print may print it again.</b> The file is looked up by name
    /// among the caller's own, so on somebody else's print this would queue <i>your</i> file of that
    /// name - hence <c>403</c>, even for a caller who may read the row and use the printer.
    /// </para>
    /// <para>
    /// <b>A file that has gone is <c>409</c>, not <c>404</c></b>: the print this URL names exists, and
    /// what is missing is the thing it would print. The new entry has a handle of its own, and the
    /// response carries any warnings about the file and the printer, as <c>POST …/queue</c> does.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("printers/{printerUuid:guid}/jobs/{printUuid:guid}/reprint")]
    public async Task<Results<Created<EnqueuedPrintReadDTO>, ForbiddenProblem, NotFoundProblem, ConflictProblem>> Reprint(
        Guid printerUuid,
        Guid printUuid,
        CancellationToken cancellationToken)
    {
        (Printer? printer, Caller? caller) = await ResolveAsync(printerUuid, cancellationToken);

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
            EnqueueOutcome? outcome = await _queue.ReprintAsync(printer.Id, printUuid, caller, cancellationToken);

            if (outcome is null)
            {
                return this.NotFoundProblem();
            }

            return TypedResults.Created(Url.Action(nameof(PrintQueueController.List), "PrintQueue", new { printerUuid }),
                                        EnqueuedPrintReadDTO.FromOutcome(outcome, _errors.For));
        }
        catch (PrintNotYoursException e)
        {
            return this.ForbiddenProblem(_errors.For(e));
        }
        catch (PrintFileNotFoundException e)
        {
            return this.ConflictProblem(_errors.For(e));
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
    /// telling them apart would confirm the existence of other people's printers.
    /// </remarks>
    private async Task<(Printer? printer, Caller? caller)> ResolveAsync(Guid printerUuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return (null, null);
        }

        Caller caller = CallerResolver.For(user, User);
        Printer? printer = await _printers.GetPrinterForUserAsync(printerUuid, caller, cancellationToken);

        return (printer, caller);
    }
}
