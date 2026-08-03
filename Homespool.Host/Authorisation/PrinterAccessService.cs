using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Exceptions;
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
/// see each one.
/// </para>
/// </remarks>
public class PrinterAccessService
{
    private readonly HSDbContext _dbContext;

    /// <summary>
    /// Memberships already looked up in this scope, including the misses.
    /// </summary>
    /// <remarks>
    /// A request cannot change its own permissions part-way through, and the pages here ask the same
    /// question repeatedly: the printer Detail page reads its own <c>CanUse</c> and then calls four
    /// services that each independently reloaded printer and membership - five lookups for one answer.
    /// Scoped lifetime bounds this to a request, so there is nothing to invalidate.
    /// <para>
    /// Null values are cached too. "Not a member" is an answer, and re-asking it on every call was
    /// most of the waste on a page a stranger cannot see.
    /// </para>
    /// </remarks>
    private readonly Dictionary<(int printerId, long userId), TeamMember?> _memberships = [];

    private readonly Dictionary<int, Printer?> _printers = [];

    public PrinterAccessService(HSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// The permission an operation currently needs. <b>The whole operation-to-flag mapping, in one
    /// place</b> - which is the reason <see cref="PrinterOperation"/> names operations at all.
    /// </summary>
    public static Func<TeamMember, bool> RequiredPermission(PrinterOperation operation)
    {
        return operation switch
        {
            PrinterOperation.ViewPrinter or PrinterOperation.ViewQueue or PrinterOperation.ViewHistory =>
                member => member.CanRead,

            PrinterOperation.ChangeQueue or PrinterOperation.ControlPrinter =>
                member => member.CanUse,

            PrinterOperation.ManagePrinter =>
                member => member.CanManage,

            // A new operation with no mapping is a programming error, and a silent default of
            // CanRead would be the wrong one to guess: it would grant, not refuse.
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "No permission is mapped to this operation."),
        };
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
    public async Task<Printer> RequireAsync(int printerId, long userId, PrinterOperation operation,
        CancellationToken cancellationToken)
    {
        Printer? printer = await PrinterAsync(printerId, cancellationToken);

        if (printer is null)
        {
            throw PrinterNotFoundException.ForId(printerId);
        }

        if (!await AllowsAsync(printerId, userId, operation, cancellationToken))
        {
            throw new TeamAccessDeniedException();
        }

        return printer;
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
    /// tree has nowhere to put a <see cref="Func{T, TResult}"/>.
    /// </para>
    /// </remarks>
    public async Task<Printer?> FindAsync(Guid uuid, long userId, PrinterOperation operation,
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

        return await AllowsAsync(printer.Id, userId, operation, cancellationToken) ? printer : null;
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
    public async Task<bool> AllowsAsync(int printerId, long userId, PrinterOperation operation,
        CancellationToken cancellationToken)
    {
        Printer? printer = await PrinterAsync(printerId, cancellationToken);

        if (printer is null)
        {
            return false;
        }

        TeamMember? membership = await MembershipAsync(printerId, printer.TeamId, userId, cancellationToken);

        return membership is not null && RequiredPermission(operation).Invoke(membership);
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

    private async Task<TeamMember?> MembershipAsync(int printerId, int teamId, long userId,
        CancellationToken cancellationToken)
    {
        if (_memberships.TryGetValue((printerId, userId), out TeamMember? cached))
        {
            return cached;
        }

        // AsNoTracking, unlike TeamService.GetMemberAsync: a cached instance outlives the call that
        // fetched it, and a tracked one could be mutated by an unrelated save in the same scope -
        // which would silently change an answer already given.
        TeamMember? membership = await _dbContext.TeamMembers
                                                 .AsNoTracking()
                                                 .SingleOrDefaultAsync(
                                                      member => member.TeamId == teamId && member.UserId == userId,
                                                      cancellationToken);

        _memberships[(printerId, userId)] = membership;

        return membership;
    }
}
