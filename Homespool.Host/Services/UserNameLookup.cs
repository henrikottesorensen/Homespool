using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;

namespace Homespool.Host.Services;

/// <summary>
/// Usernames for a handful of user ids, for pages that show who did something.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lookup rather than a join, because the rows that carry these ids deliberately do not point at
/// users.</b> <c>PrintJob.StoppedByUserId</c> and <c>QueuedPrint.QueuedByUserId</c> have no foreign
/// key and no navigation - see those entities for why - so a name is fetched for the few ids a page is
/// showing rather than included per row.
/// </para>
/// <para>
/// <b>It authorises nothing, and must not be asked to.</b> A caller reaching a row that carries an id
/// has already been through the service that owns it. This adds no way to ask about a user you could
/// not already see the trace of, and it answers only for ids you hand it.
/// </para>
/// <para>
/// <b>An id with no row comes back absent rather than blank</b>, which is what lets a caller tell
/// "somebody we can no longer name" from "nobody" - two different facts, and an account is never hard
/// deleted, so the absent case is rarer than it looks.
/// </para>
/// </remarks>
public sealed class UserNameLookup
{
    private readonly HomespoolDbContext _dbContext;

    public UserNameLookup(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Names for the given ids, keyed by id. Ids with no readable account are absent.</summary>
    public async Task<IReadOnlyDictionary<long, string>> ForAsync(IEnumerable<long> userIds,
                                                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        long[] ids = userIds.Distinct().ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<long, string>();
        }

        return await _dbContext.Users
                               .AsNoTracking()
                               .Where(user => ids.Contains(user.Id) && user.UserName != null)
                               .ToDictionaryAsync(user => user.Id, user => user.UserName!, cancellationToken);
    }
}
