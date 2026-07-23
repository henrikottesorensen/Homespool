using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using PrinterService.Data;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Services;

/// <summary>
/// Wraps <see cref="PSDbContext"/> access for team and team-membership operations, so callers
/// (Razor Pages, controllers) never hold the context directly.
/// </summary>
public class TeamService
{
    private readonly PSDbContext _dbContext;

    public TeamService(PSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Stages a default team for <paramref name="userId"/> via <see cref="TeamProvisioning.AddDefaultTeam"/>
    /// and saves. Exceptions propagate - callers doing this alongside other writes (e.g. Identity user
    /// creation) should wrap the whole sequence in a transaction from <see cref="UnitOfWork.BeginTransactionAsync"/>
    /// so a failure here rolls back everything instead of needing a compensating delete.
    /// </summary>
    public Task AddDefaultTeamAsync(long userId, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        _dbContext.AddDefaultTeam(userId, createdAt);

        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<TeamMember> AddMemberAsync(int teamId, long userId, bool canRead, bool canUse, bool canManage, CancellationToken cancellationToken)
    {
        TeamMember member = new()
        {
            TeamId = teamId,
            UserId = userId,
            CanRead = canRead,
            CanUse = canUse,
            CanManage = canManage,
            IsDefault = false,
        };

        _dbContext.TeamMembers.Add(member);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return member;
    }

    /// <summary>
    /// Removes the (<paramref name="teamId"/>, <paramref name="userId"/>) membership if it exists and
    /// saves. Returns <c>false</c> without error if the membership doesn't exist.
    /// </summary>
    public async Task<bool> RemoveMemberAsync(int teamId, long userId, CancellationToken cancellationToken)
    {
        TeamMember? member = await _dbContext.TeamMembers.FindAsync([teamId, userId], cancellationToken);

        if (member is null)
        {
            return false;
        }

        _dbContext.TeamMembers.Remove(member);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public Task<TeamMember?> GetMemberAsync(int teamId, long userId, CancellationToken cancellationToken)
    {
        return _dbContext.TeamMembers.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);
    }

    public Task<List<TeamMember>> GetMembersAsync(int teamId, CancellationToken cancellationToken)
    {
        return _dbContext.TeamMembers.Where(m => m.TeamId == teamId).ToListAsync(cancellationToken);
    }
}
