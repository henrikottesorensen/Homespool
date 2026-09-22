using System.Linq;

using Homespool.Data;

namespace Homespool.Host.Accounts;

/// <summary>
/// Who is an administrator: an account holding the <see cref="AdminBootstrap.AdminRole"/> row that
/// is still open. The one definition every decision about administering asks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the database, not the cookie.</b> The role claim is what the cookie was issued with,
/// not what the account is now. It is a cheap first filter for the callers that have one; it is
/// never the answer.
/// </para>
/// <para>
/// <b>What this is not for.</b> A badge saying an account holds the role describes the subject of a
/// page, and reads the role row directly - a closed administrator still holds it. This answers
/// whether somebody may act as one.
/// </para>
/// </remarks>
public static class Administrators
{
    /// <summary>The ids of every open administrator.</summary>
    public static IQueryable<long> Open(HomespoolDbContext dbContext)
    {
        return (from membership in dbContext.UserRoles
                join role in dbContext.Roles on membership.RoleId equals role.Id
                join account in dbContext.Users on membership.UserId equals account.Id
                where role.Name == AdminBootstrap.AdminRole && account.DeactivatedAt == null
                select account.Id)
               .Distinct();
    }
}
