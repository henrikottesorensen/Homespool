using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin.Users;

/// <summary>
/// Every account on this deployment, what it can sign in with, and whether it may.
/// </summary>
/// <remarks>
/// <para>
/// <b>A roster, not a control panel.</b> Acting on an account happens on <c>Detail</c>, which shows
/// what is about to be affected and asks the administrator to prove themselves first. Buttons here
/// would mean either a password field per row or a page-wide one detached from what it authorises -
/// and a list is where somebody clicks the row above the one they meant.
/// </para>
/// <para>
/// <b>It reads whole and shapes in memory</b>, following the passkey listing it absorbed: one to tens
/// of accounts, so the counts are three grouped queries rather than a per-row read, and nothing
/// needs paging or search.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class IndexModel : PageModel
{
    private readonly HomespoolDbContext _context;
    private readonly TimeProvider _time;

    public IndexModel(HomespoolDbContext context, TimeProvider time)
    {
        _context = context;
        _time = time;
    }

    /// <summary>One account, as the roster shows it.</summary>
    /// <param name="Id">The account, for the link to its detail page.</param>
    /// <param name="UserName">What the interface calls this person.</param>
    /// <param name="Email">The address sign-in also accepts, and where recovery mail goes.</param>
    /// <param name="EmailConfirmed">An unconfirmed address is one this account cannot yet sign in on.</param>
    /// <param name="IsAdministrator">Whether the account holds the one privileged role.</param>
    /// <param name="TwoFactorEnabled">Whether an authenticator is enrolled.</param>
    /// <param name="HasPassword">False for an account whose only credential is its identity provider.</param>
    /// <param name="Passkeys">How many passkeys are enrolled.</param>
    /// <param name="Tokens">How many API tokens are outstanding.</param>
    /// <param name="Deactivated">Whether an administrator has closed this account.</param>
    /// <param name="LockedOut">Whether failed sign-ins are currently holding it out.</param>
    public sealed record Row(long Id,
                             string UserName,
                             string? Email,
                             bool EmailConfirmed,
                             bool IsAdministrator,
                             bool TwoFactorEnabled,
                             bool HasPassword,
                             int Passkeys,
                             int Tokens,
                             bool Deactivated,
                             bool LockedOut);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();

        List<HSUser> accounts = await _context.Users
                                              .AsNoTracking()
                                              .ToListAsync(cancellationToken);

        HashSet<long> administrators = await AdministratorIdsAsync(cancellationToken);

        Dictionary<long, int> tokens = await _context.ApiTokens
                                                     .GroupBy(token => token.UserId)
                                                     .Select(group => new { UserId = group.Key, Count = group.Count() })
                                                     .ToDictionaryAsync(entry => entry.UserId, entry => entry.Count, cancellationToken);

        Dictionary<long, int> passkeys = await _context.Set<IdentityUserPasskey<long>>()
                                                       .GroupBy(passkey => passkey.UserId)
                                                       .Select(group => new { UserId = group.Key, Count = group.Count() })
                                                       .ToDictionaryAsync(entry => entry.UserId, entry => entry.Count, cancellationToken);

        Rows =
        [
            .. accounts.Select(account => new Row(
                                   account.Id,
                                   account.UserName ?? string.Empty,
                                   account.Email,
                                   account.EmailConfirmed,
                                   administrators.Contains(account.Id),
                                   account.TwoFactorEnabled,
                                   account.PasswordHash is not null,
                                   passkeys.GetValueOrDefault(account.Id),
                                   tokens.GetValueOrDefault(account.Id),
                                   account.DeactivatedAt is not null,
                                   account.LockoutEnd > now))
                       .OrderBy(row => row.UserName, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The accounts holding the administrator role, deactivated ones included - the roster says who
    /// <em>is</em> an administrator, and whether they can currently sign in is the other column.
    /// </summary>
    private async Task<HashSet<long>> AdministratorIdsAsync(CancellationToken cancellationToken)
    {
        List<long> ids = await (from membership in _context.UserRoles
                                join role in _context.Roles on membership.RoleId equals role.Id
                                where role.Name == AdminBootstrap.AdminRole
                                select membership.UserId)
                               .ToListAsync(cancellationToken);

        return [.. ids];
    }
}
