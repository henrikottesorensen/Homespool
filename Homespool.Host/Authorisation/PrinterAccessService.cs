using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// May this account do this to this printer - the one implementation of a question that was being
/// answered in six places.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than as an ASP.NET authorisation policy, and the reason is what the question is
/// about.</b> <see cref="Policies"/> answers "who are you, and by which scheme" - genuinely the
/// framework's business, and it stays there. This answers "what may this account do to this row",
/// which is a fact about <see cref="TeamMember"/> and the domain. The framework's own mechanism for
/// the second kind is resource-based authorisation, which is an <i>imperative call</i> in the middle
/// of a method rather than an attribute on an endpoint - so it buys no declarative check here, while
/// costing a <c>ClaimsPrincipal</c> these services deliberately do not take.
/// </para>
/// <para>
/// <b>What it does buy, and the argument this rests on: the check cannot be skipped.</b> With the
/// gate inside the services, <see cref="Queue.PrintQueueService.EnqueueAsync"/> cannot be reached
/// unchecked - not from a controller, a Razor page, a background job, or a route somebody adds next
/// year. At the edge it becomes a thing each new caller has to remember, and a forgotten permission
/// check fails silently in the dangerous direction.
/// </para>
/// <para>
/// <b>In <c>Authorisation/</c> beside <see cref="Policies"/> deliberately.</b> The honest cost of a
/// domain gate is that it is less discoverable than <c>[Authorize]</c> - somebody new greps for the
/// attribute and concludes the policies are the whole story. Keeping both in one folder is the
/// mitigation: whoever goes looking for how this application decides who may do what finds the
/// framework half and the domain half together.
/// </para>
/// <para>
/// <b>Three entry points, because there are three honest answers to "you may not".</b> They are not
/// an accident of how the six sites grew, and collapsing them would lose a deliberate distinction -
/// see each one. <see cref="AllowsWithdrawingAsync"/> and its throwing twin sit alongside them
/// answering a different question: not "may you do this here" but "is this yours to withdraw".
/// </para>
/// </remarks>
public class PrinterAccessService
{
    private readonly HomespoolDbContext _dbContext;

    /// <summary>
    /// Memberships already looked up in this scope, including the misses.
    /// </summary>
    /// <remarks>
    /// A request cannot change its own permissions part-way through, and the pages here ask the same
    /// question repeatedly: the printer Detail page asks for itself and then calls four
    /// services that each independently reloaded printer and membership - five lookups for one answer.
    /// Scoped lifetime bounds this to a request, so there is nothing to invalidate.
    /// <para>
    /// Null values are cached too. "Not a member" is an answer, and re-asking it on every call was
    /// most of the waste on a page a stranger cannot see.
    /// </para>
    /// </remarks>
    private readonly Dictionary<(int printerId, long userId), CapabilitySet?> _memberships = [];

    private readonly Dictionary<int, Printer?> _printers = [];

    private readonly ILogger<PrinterAccessService> _logger;

    public PrinterAccessService(HomespoolDbContext dbContext, ILogger<PrinterAccessService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// The printer, or an exception saying which way it failed. For callers who have already been
    /// told this printer exists.
    /// </summary>
    /// <remarks>
    /// <b>Safe to distinguish "no such printer" from "not allowed" here</b>, because reaching this
    /// with an id in hand generally means something already resolved it for this caller. Where that
    /// is not true - a UUID straight off the wire - use <see cref="FindAsync"/>, which refuses to
    /// tell the two apart.
    /// </remarks>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">The caller may not do this to it.</exception>
    public async Task<Printer> RequireAsync(int printerId,
                                            long userId,
                                            Capability capability,
                                            CancellationToken cancellationToken)
    {
        Printer? printer = await PrinterAsync(printerId, cancellationToken);

        if (printer is null)
        {
            throw PrinterNotFoundException.ForId(printerId);
        }

        if (!await AllowsAsync(printerId, userId, capability, cancellationToken))
        {
            throw new TeamAccessDeniedException();
        }

        return printer;
    }

    /// <summary>
    /// May this account withdraw this piece of work - stop the print, or cancel the queue entry -
    /// given who put it there? <b>The one place the "your own work" half of
    /// <see cref="Capability.Print"/> is decided</b>.
    /// </summary>
    /// <remarks>
    /// <see cref="Capability.ControlPrinter"/> withdraws anyone's work; <see cref="Capability.Print"/>
    /// withdraws only its holder's. Here rather than at the two call sites for the reason this whole
    /// class exists: a rule spread across callers is one a new caller can forget, and forgetting this
    /// one fails open.
    /// </remarks>
    public async Task<bool> AllowsWithdrawingAsync(int printerId,
                                                   long userId,
                                                   long queuedByUserId,
                                                   CancellationToken cancellationToken)
    {
        if (await AllowsAsync(printerId, userId, Capability.ControlPrinter, cancellationToken))
        {
            return true;
        }

        return queuedByUserId == userId
               && await AllowsAsync(printerId, userId, Capability.Print, cancellationToken);
    }

    /// <summary>
    /// <see cref="AllowsWithdrawingAsync"/>, throwing the refusal the services here already speak.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">The caller may not withdraw this work.</exception>
    public async Task RequireWithdrawingAsync(int printerId,
                                              long userId,
                                              long queuedByUserId,
                                              CancellationToken cancellationToken)
    {
        if (!await AllowsWithdrawingAsync(printerId, userId, queuedByUserId, cancellationToken))
        {
            throw new TeamAccessDeniedException();
        }
    }

    /// <summary>
    /// The printer, or <c>null</c> for both "no such printer" and "not yours" - which are
    /// deliberately indistinguishable.
    /// </summary>
    /// <remarks>
    /// <b>The refusal shape exists to stop a 404 confirming a UUID.</b> A caller who could tell the
    /// two apart could enumerate other people's printers by watching which id answers differently,
    /// so this one answer covers both and no exception names the difference.
    /// <para>
    /// <b>Resolve-then-check, where <c>PrinterQueryService.GetPrinterForUserAsync</c> fused the two
    /// into one query.</b> That saved a round trip on a single call and cost one on every call after
    /// it, because the fused form answers the question without leaving anything behind. This seeds
    /// the caches instead, which is the better trade on the pages that ask repeatedly - and it keeps
    /// the operation-to-permission mapping in one place, which a query cannot do: an EF expression
    /// tree has nowhere to put the capability test.
    /// </para>
    /// </remarks>
    public async Task<Printer?> FindAsync(Guid uuid,
                                          long userId,
                                          Capability capability,
                                          CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(candidate => candidate.Uuid == uuid,
                                                                 cancellationToken);

        if (printer is null)
        {
            return null;
        }

        _printers[printer.Id] = printer;

        return await AllowsAsync(printer.Id, userId, capability, cancellationToken) ? printer : null;
    }

    /// <summary>
    /// Whether the caller may, without deciding what to do about it. <b>For asking on behalf of a
    /// page</b> - "should these controls render" is a presentation question, and the answer to it is
    /// a bool rather than an exception.
    /// </summary>
    /// <remarks>
    /// A printer that does not exist answers <c>false</c>: nobody may act on it, which is the right
    /// answer to the question actually asked and keeps this free of the not-found/not-allowed split
    /// the other two entry points care about.
    /// </remarks>
    public async Task<bool> AllowsAsync(int printerId,
                                        long userId,
                                        Capability capability,
                                        CancellationToken cancellationToken)
    {
        Printer? printer = await PrinterAsync(printerId, cancellationToken);

        if (printer is null)
        {
            return false;
        }

        CapabilitySet? capabilities = await MembershipAsync(printerId, printer.TeamId, userId, cancellationToken);

        return capabilities is not null && capabilities.Allows(capability);
    }

    private async Task<Printer?> PrinterAsync(int printerId, CancellationToken cancellationToken)
    {
        if (_printers.TryGetValue(printerId, out Printer? cached))
        {
            return cached;
        }

        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(candidate => candidate.Id == printerId,
                                                                 cancellationToken);

        _printers[printerId] = printer;

        return printer;
    }

    /// <summary>
    /// The caller's capabilities on this printer's team, or <c>null</c> if they are not a member -
    /// which is a different answer from a member holding nothing.
    /// </summary>
    private async Task<CapabilitySet?> MembershipAsync(int printerId,
                                                       int teamId,
                                                       long userId,
                                                       CancellationToken cancellationToken)
    {
        if (_memberships.TryGetValue((printerId, userId), out CapabilitySet? cached))
        {
            return cached;
        }

        // Only the column: nothing here needs the row, and projecting keeps a tracked entity from
        // being mutated by an unrelated save in the same scope, which would silently change an answer
        // already given.
        string? stored = await _dbContext.TeamMembers
                                         .AsNoTracking()
                                         .Where(member => member.TeamId == teamId && member.UserId == userId)
                                         .Select(member => member.Capabilities)
                                         .SingleOrDefaultAsync(cancellationToken);

        CapabilitySet? capabilities = stored is null ? null : CapabilitySet.Parse(stored);

        if (capabilities is not null && capabilities.Unrecognised.Length > 0)
        {
            // Grants nothing, so this has already failed closed - but silently, and the causes are a
            // hand-edited row or a Capability rename whose data migration was missed. Both want a
            // person to know.
            _logger.LogWarning(
                "Team {TeamId} membership for user {UserId} carries unrecognised capabilities: {Unrecognised}.",
                teamId,
                userId,
                string.Join(", ", capabilities.Unrecognised));
        }

        _memberships[(printerId, userId)] = capabilities;

        return capabilities;
    }
}
