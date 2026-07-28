using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Homespool.Host.Services;

/// <summary>
/// Team-permission-checked reads and edits of <see cref="Printer"/>, for the app-facing
/// <c>GET/PATCH /api/v1/printers</c> surface (phase-1.5 §15 step 7b). Kept separate from
/// <see cref="PrusaConnect.PrusaConnectService"/>, which owns the registration protocol and the
/// claim that creates a printer - this owns everything that happens to one afterwards.
/// </summary>
public class PrinterQueryService
{
    /// <summary>Rows shown on the printer detail page - a display choice, not a protocol knob, so
    /// fixed constants rather than a config option.</summary>
    private const int RecentSampleCount = 50;
    private const int RecentEventCount = 20;

    private readonly HSDbContext _dbContext;

    public PrinterQueryService(HSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Every printer belonging to a team <paramref name="userId"/> has <c>CanRead</c> on, oldest
    /// first. Read-only, so untracked.
    /// </summary>
    public async Task<IReadOnlyList<Printer>> ListPrintersForUserAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Printers
                               .AsNoTracking()
                               .Where(p => _dbContext.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == userId && m.CanRead))
                               .OrderBy(p => p.Id)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// As <see cref="ListPrintersForUserAsync"/>, but pairs each printer with its
    /// <see cref="PrinterLiveState"/> - which is where a printer's <em>actual</em> status lives.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Printer.Status"/> is not a live value and never has been.</b> It is written once,
    /// as <see cref="PrinterStatus.Unknown"/>, when the printer row is created, and nothing updates it
    /// afterwards; telemetry writes <see cref="PrinterLiveState.Status"/> instead, on a different
    /// entity. Anything reporting a printer's state to a caller has to join to that, which is what
    /// this exists for. Null live state means the printer has never connected.
    /// </remarks>
    public async Task<IReadOnlyList<PrinterWithState>> ListPrintersWithStateForUserAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Printers
                               .AsNoTracking()
                               .Where(p => _dbContext.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == userId && m.CanRead))
                               .OrderBy(p => p.Id)
                               .Select(p => new PrinterWithState(
                                   p,
                                   _dbContext.PrinterLiveStates.SingleOrDefault(s => s.PrinterId == p.Id)))
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// As <see cref="GetPrinterForUserAsync"/>, paired with its <see cref="PrinterLiveState"/>. See
    /// <see cref="ListPrintersWithStateForUserAsync"/> for why the join is necessary rather than
    /// convenient.
    /// </summary>
    public Task<PrinterWithState?> GetPrinterWithStateForUserAsync(Guid uuid, long userId, CancellationToken cancellationToken)
    {
        return _dbContext.Printers
                         .AsNoTracking()
                         .Where(p => p.Uuid == uuid &&
                                     _dbContext.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == userId && m.CanRead))
                         .Select(p => new PrinterWithState(
                             p,
                             _dbContext.PrinterLiveStates.SingleOrDefault(s => s.PrinterId == p.Id)))
                         .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A single printer by its public <see cref="Printer.Uuid"/>, or <c>null</c> if it doesn't
    /// exist <b>or</b> the caller lacks <c>CanRead</c> on its team - the two cases are
    /// indistinguishable on purpose, so a 404 never confirms a UUID belongs to someone else's team.
    /// </summary>
    /// <remarks>
    /// Returns the printer alone, which is all a caller resolving one for a permission check needs -
    /// <c>PrinterTransferController</c> is the example. A caller that reports state to a user wants
    /// <see cref="GetPrinterWithStateForUserAsync"/> instead.
    /// </remarks>
    public Task<Printer?> GetPrinterForUserAsync(Guid uuid, long userId, CancellationToken cancellationToken)
    {
        return _dbContext.Printers
                         .AsNoTracking()
                         .SingleOrDefaultAsync(p => p.Uuid == uuid &&
                                                    _dbContext.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == userId && m.CanRead),
                                               cancellationToken);
    }

    /// <summary>
    /// Updates a printer's user-editable fields - name and location only; moving it between teams
    /// is deferred (needs a permission check on both the old and new team). Returns <c>null</c> under
    /// the same "doesn't exist or caller can't even read it" rule as <see cref="GetPrinterForUserAsync"/>.
    /// A caller who can read but not manage the printer's team gets <see cref="TeamAccessDeniedException"/>
    /// instead - safe to distinguish, since reaching that branch already proves they can see the printer.
    /// </summary>
    /// <remarks>
    /// Returns the live state alongside, so the response to an edit describes the same resource the
    /// next <c>GET</c> will - a PATCH answering <c>UNKNOWN</c> while a GET one second later says
    /// <c>PRINTING</c> would look like the edit had reset something.
    /// </remarks>
    public async Task<PrinterWithState?> UpdatePrinterAsync(Guid uuid, long userId, string? name, string? location, CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers.SingleOrDefaultAsync(p => p.Uuid == uuid, cancellationToken);

        if (printer is null)
        {
            return null;
        }

        TeamMember? membership = await _dbContext.TeamMembers
            .SingleOrDefaultAsync(m => m.TeamId == printer.TeamId && m.UserId == userId, cancellationToken);

        if (membership is null || !membership.CanRead)
        {
            return null;
        }

        if (!membership.CanManage)
        {
            throw new TeamAccessDeniedException();
        }

        printer.Name = name;
        printer.Location = location;
        printer.UpdatedAt = TimeProvider.System.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken);

        PrinterLiveState? liveState = await _dbContext.PrinterLiveStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.PrinterId == printer.Id, cancellationToken);

        return new PrinterWithState(printer, liveState);
    }

    /// <summary>
    /// A printer's live state plus recent telemetry/event history, for the detail page. Same
    /// "doesn't exist or caller can't even read it" rule as <see cref="GetPrinterForUserAsync"/> -
    /// both cases return <c>null</c> so a 404 never confirms a UUID belongs to someone else's team.
    /// </summary>
    public async Task<PrinterStatistics?> GetPrinterStatisticsForUserAsync(Guid uuid, long userId, CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers
            .AsNoTracking()
            .Include(p => p.Team)
            .SingleOrDefaultAsync(p => p.Uuid == uuid &&
                                       _dbContext.TeamMembers.Any(m => m.TeamId == p.TeamId && m.UserId == userId && m.CanRead),
                                  cancellationToken);

        if (printer is null)
        {
            return null;
        }

        PrinterLiveState? liveState = await _dbContext.PrinterLiveStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.PrinterId == printer.Id, cancellationToken);

        List<TelemetrySample> samples = await _dbContext.TelemetrySamples
            .AsNoTracking()
            .Where(s => s.PrinterId == printer.Id)
            .OrderByDescending(s => s.Timestamp)
            .Take(RecentSampleCount)
            .ToListAsync(cancellationToken);

        List<PrinterEvent> events = await _dbContext.PrinterEvents
            .AsNoTracking()
            .Where(e => e.PrinterId == printer.Id)
            .OrderByDescending(e => e.Timestamp)
            .Take(RecentEventCount)
            .ToListAsync(cancellationToken);

        return new PrinterStatistics(printer, liveState, samples, events);
    }
}

/// <summary>A printer paired with its last-known state, which may be absent if it has never
/// connected. See <see cref="PrinterQueryService.ListPrintersWithStateForUserAsync"/>.</summary>
public sealed record PrinterWithState(Printer Printer, PrinterLiveState? LiveState);

/// <summary>Result of <see cref="PrinterQueryService.GetPrinterStatisticsForUserAsync"/> - a
/// printer's live state plus recent history, newest first.</summary>
public sealed record PrinterStatistics(
    Printer Printer,
    PrinterLiveState? LiveState,
    IReadOnlyList<TelemetrySample> RecentSamples,
    IReadOnlyList<PrinterEvent> RecentEvents);
