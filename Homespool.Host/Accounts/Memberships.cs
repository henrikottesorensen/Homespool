using System.Linq;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// The team memberships that grant anything: those of accounts still open. The one definition every
/// decision about what a member may do asks.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed account stays listed and can do nothing.</b> Its rows are kept, because history names
/// the people who queued and stopped prints, and a roster that dropped them would misdescribe that
/// history. They are left out only when the question is authority.
/// </para>
/// <para>
/// <b>An inner join, so a row with no account behind it grants nothing either.</b>
/// <see cref="TeamMember.UserId"/> is a plain id rather than a foreign key, and nothing deletes an
/// account, so such a row exists only after somebody removed an account by hand - and then the
/// only thing left that could act on it is the queue loop, on authority nobody can still answer for.
/// </para>
/// <para>
/// <b>Here and not only at sign-in, because not every caller signed in.</b> The queue loop acts as
/// whoever queued the print, long after the request that queued it, and a closed account's sessions
/// and tokens ending does nothing to authority recorded on a queue entry.
/// </para>
/// </remarks>
public static class Memberships
{
    /// <summary>Every membership row whose account is open.</summary>
    public static IQueryable<TeamMember> Open(HomespoolDbContext dbContext)
    {
        return from member in dbContext.TeamMembers
               join account in dbContext.Users on member.UserId equals account.Id
               where account.DeactivatedAt == null
               select member;
    }
}
