using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// Which teams does this account hold a given capability on? <b>The answer a listing query needs</b>,
/// where <see cref="PrinterAccessService"/> answers about one row at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved in memory, not in SQL, and that is the point.</b> Capabilities are a space-separated
/// string, so the obvious translation of the old <c>member.CanRead</c> predicate is a padded
/// <c>LIKE</c> - which is not indexable, and which matches one capability's name inside another's the
/// day two of them share a prefix. Reading the membership rows first and filtering here is exact,
/// costs one small query, and leaves the caller filtering printers on <c>TeamId</c>, which is indexed
/// (<c>IX_Printers_TeamId</c>).
/// </para>
/// <para>
/// <b>Not a denormalised boolean column.</b> That was the other way to keep the predicate in SQL, and
/// it would put authority in two places - the mistake <c>Printer.Owner</c> was removed for.
/// </para>
/// <para>
/// <b>The table is small by construction.</b> One row per user per team, in a deployment sized for
/// one to tens of printers, so this reads single digits of rows.
/// </para>
/// </remarks>
public class TeamCapabilityLookup
{
    private readonly HomespoolDbContext _dbContext;

    /// <summary>
    /// Answers already given in this scope. A request cannot change its own permissions part-way
    /// through, and the pages here ask the same question from several services.
    /// </summary>
    private readonly Dictionary<(long userId, Capability capability), IReadOnlyCollection<int>> _teams = [];

    public TeamCapabilityLookup(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// The ids of every team on which <paramref name="userId"/> holds <paramref name="capability"/>.
    /// Empty when they hold it nowhere, which callers should treat as "match no rows" rather than
    /// skipping the filter.
    /// </summary>
    public async Task<IReadOnlyCollection<int>> TeamsAllowingAsync(long userId,
                                                                   Capability capability,
                                                                   CancellationToken cancellationToken)
    {
        if (_teams.TryGetValue((userId, capability), out IReadOnlyCollection<int>? cached))
        {
            return cached;
        }

        List<TeamMember> memberships = await _dbContext.TeamMembers
                                                       .AsNoTracking()
                                                       .Where(member => member.UserId == userId)
                                                       .ToListAsync(cancellationToken)
                                                       .ConfigureAwait(false);

        List<int> allowed = memberships
                            .Where(member => CapabilitySet.Parse(member.Capabilities).Allows(capability))
                            .Select(member => member.TeamId)
                            .ToList();

        _teams[(userId, capability)] = allowed;

        return allowed;
    }
}
