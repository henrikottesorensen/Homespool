using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Recomputes every account's <c>NormalizedUserName</c> at start-up, so the lookup key is always the
/// one the current <see cref="SkeletonLookupNormalizer"/> and the current Unicode data would produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every start-up, unconditionally.</b> The key is derived: a database written before the
/// skeleton became the key holds plain upper-cased names, and a Squint upgrade can change a
/// skeleton when its confusable table changes. Either leaves a row whose key no longer matches what
/// <c>FindByNameAsync</c> computes, and that account can no longer sign in by name. Recomputing tens
/// of rows takes microseconds each, which is cheaper than any scheme that remembers whether it was
/// done.
/// </para>
/// <para>
/// <b>A collision is reported, never resolved here.</b> If two existing accounts share a skeleton
/// - <em>henrik</em> and a Cyrillic-<c>а</c> <em>henrik</em>, say - writing the new key for both
/// would trip the unique index and stop the service. Both rows are left as they are and an error
/// names them, so an administrator decides which account renames; every other row is still
/// refreshed. Accounts left on an old key still sign in by email, and by name whenever the old key
/// happens to equal the new one, which for plain ASCII it does.
/// </para>
/// <para>
/// Goes through the store rather than <see cref="UserManager{TUser}"/>, because the manager runs the
/// validators on every update and this is not a change to any name.
/// </para>
/// </remarks>
public static class UsernameKeyRefresh
{
    /// <summary>
    /// Runs once at start-up, after the migration and the admin bootstrap. Inline for the same reason
    /// as <see cref="AdminBootstrap.SeedAdminBootstrap"/>: a sign-in must not race it.
    /// </summary>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification = "Deliberately synchronous, as AdminBootstrap is: no request may sign in against a stale key.")]
    public static void RefreshUsernameKeys(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        RefreshAsync(services, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The refresh itself, on a scope of the caller's. Returns how many keys were rewritten.
    /// </summary>
    public static async Task<int> RefreshAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();

        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                              .CreateLogger(typeof(UsernameKeyRefresh).FullName!);
        ILookupNormalizer normaliser = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        List<HSUser> users = await context.Users.ToListAsync(cancellationToken);

        List<(HSUser user, string key)> keyed = users.Where(u => u.UserName is not null)
                                                     .Select(u => (u, normaliser.NormalizeName(u.UserName)!))
                                                     .ToList();

        HashSet<string> collidingKeys = keyed.GroupBy(k => k.key, StringComparer.Ordinal)
                                             .Where(g => g.Count() > 1)
                                             .Select(g => g.Key)
                                             .ToHashSet(StringComparer.Ordinal);

        foreach (IGrouping<string, (HSUser user, string key)> group in keyed.GroupBy(k => k.key, StringComparer.Ordinal)
                                                                         .Where(g => collidingKeys.Contains(g.Key)))
        {
            logger.LogError(
                "Usernames {UserNames} look alike and would share the lookup key {Key}; none of them was refreshed. Rename one of them.",
                string.Join(", ", group.Select(k => $"'{k.user.UserName}' (id {k.user.Id})")),
                group.Key);
        }

        int rewritten = 0;

        foreach ((HSUser user, string key) in keyed.Where(k => !collidingKeys.Contains(k.key)))
        {
            if (string.Equals(user.NormalizedUserName, key, StringComparison.Ordinal))
            {
                continue;
            }

            user.NormalizedUserName = key;
            rewritten++;
        }

        if (rewritten > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Refreshed the lookup key of {Count} account(s).", rewritten);
        }

        return rewritten;
    }
}
