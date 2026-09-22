using System.Globalization;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Open account rows for fixtures whose memberships only need somebody behind them.
/// </summary>
/// <remarks>
/// A membership grants nothing without an open account: <c>TeamMember.UserId</c> is a plain id, and
/// the access services join it to the account to ask whether it is closed. A fixture seeding a
/// membership alone is seeding one that refuses.
/// </remarks>
internal static class TestAccounts
{
    /// <summary>
    /// Adds an open account for each id that has none yet, leaving the save to the caller.
    /// </summary>
    public static void Add(HomespoolDbContext context, params long[] userIds)
    {
        foreach (long userId in userIds)
        {
            if (context.Users.Find(userId) is not null)
            {
                continue;
            }

            string name = "member" + userId.ToString(CultureInfo.InvariantCulture);
            string email = name + "@example.com";

            context.Users.Add(new HSUser(name)
            {
                Id = userId,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = name.ToUpperInvariant(),
            });
        }
    }
}
