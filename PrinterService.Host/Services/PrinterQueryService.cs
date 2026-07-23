using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Services;

/// <summary>
/// Team-permission-checked reads and edits of <see cref="Printer"/>, for the app-facing
/// <c>GET/PATCH /api/v1/printers</c> surface (phase-1.5 §15 step 7b). Kept separate from
/// <see cref="PrusaConnect.PrusaConnectService"/>, which owns the registration protocol and the
/// claim that creates a printer - this owns everything that happens to one afterwards.
/// </summary>
public class PrinterQueryService
{
    private readonly PSDbContext _dbContext;

    public PrinterQueryService(PSDbContext dbContext)
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
    /// A single printer by its public <see cref="Printer.Uuid"/>, or <c>null</c> if it doesn't
    /// exist <b>or</b> the caller lacks <c>CanRead</c> on its team - the two cases are
    /// indistinguishable on purpose, so a 404 never confirms a UUID belongs to someone else's team.
    /// </summary>
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
    public async Task<Printer?> UpdatePrinterAsync(Guid uuid, long userId, string? name, string? location, CancellationToken cancellationToken)
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

        return printer;
    }
}
