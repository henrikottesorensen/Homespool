using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin.Users;

/// <summary>
/// One account, everything it can sign in with, and the four things an administrator may do to it:
/// close it, reopen it, revoke its API tokens, and lift what is holding it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>What stands between a session and these acts is <see cref="AdminElevation"/></b>, earned at
/// <c>Admin/Challenge</c> and good for ten minutes across the administration screens - so this page
/// asks for nothing itself. A password field beside every button would be retyped until it stopped
/// being read, and on a page about somebody else's account it also asks a confusing question: whose
/// password is this? The reason for having a gate at all is unchanged - a session says only that a
/// browser signed in once, and closing an account is exactly what a walk-up on an unlocked
/// administrator's browser would want to do.
/// </para>
/// <para>
/// <b>The listings are read-only except where an administrator is the only one who can act.</b>
/// Passkeys have a revoke because losing the device one lives on leaves a credential the owner
/// cannot remove; tokens revoke as a set, because the case that brings somebody to this page is a
/// compromised account rather than one awkward script. There is no per-token button for that reason,
/// and no delete for the account at all.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
[RequireAdminElevation]
public class DetailModel : PageModel
{
    private readonly HomespoolDbContext _context;
    private readonly UserManager<HSUser> _users;
    private readonly UserAdministration _administration;
    private readonly TeamService _teams;
    private readonly CapabilityText _capabilities;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly TimeProvider _time;

    public DetailModel(HomespoolDbContext context,
                       UserManager<HSUser> users,
                       UserAdministration administration,
                       TeamService teams,
                       CapabilityText capabilities,
                       IStringLocalizer<SharedResource> localiser,
                       TimeProvider time)
    {
        _context = context;
        _users = users;
        _administration = administration;
        _teams = teams;
        _capabilities = capabilities;
        _localiser = localiser;
        _time = time;
    }

    /// <summary>One of this account's passkeys.</summary>
    public sealed record PasskeyRow(string Id, string? Name, DateTimeOffset CreatedAt, bool Synced);

    /// <summary>One of this account's API tokens. Named and dated; the secret is not recoverable.</summary>
    public sealed record TokenRow(string? Name, string Scope, DateTimeOffset CreatedAt);

    /// <summary>One of this account's team memberships, and what it permits there.</summary>
    public sealed record TeamRow(string Name, string Capabilities, bool IsDefault);

    [TempData]
    public string? StatusMessage { get; set; }

    public long Id { get; private set; }

    public string UserName { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool IsAdministrator { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public bool HasPassword { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public bool LockedOut { get; private set; }

    /// <summary>Whether this account is the one the administrator reading the page signs in as.</summary>
    public bool IsSelf { get; private set; }

    public IReadOnlyList<PasskeyRow> Passkeys { get; private set; } = [];

    public IReadOnlyList<TokenRow> Tokens { get; private set; } = [];

    public IReadOnlyList<TeamRow> Teams { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken)
    {
        return await LoadAsync(id, cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(long id, CancellationToken cancellationToken)
    {
        return await ActAsync(id,
                              administratorId => _administration.DeactivateAsync(administratorId, id, cancellationToken),
                              result => result.Affected switch
                              {
                                  0 => _localiser["AdminUsers_Deactivated"].Value,
                                  1 => _localiser["AdminUsers_DeactivatedOneToken"].Value,
                                  _ => _localiser["AdminUsers_DeactivatedTokens", result.Affected].Value,
                              },
                              cancellationToken);
    }

    public async Task<IActionResult> OnPostReactivateAsync(long id, CancellationToken cancellationToken)
    {
        return await ActAsync(id,
                              administratorId => _administration.ReactivateAsync(administratorId, id, cancellationToken),
                              _ => _localiser["AdminUsers_Reactivated"].Value,
                              cancellationToken);
    }

    public async Task<IActionResult> OnPostRevokeTokensAsync(long id, CancellationToken cancellationToken)
    {
        return await ActAsync(id,
                              administratorId => _administration.RevokeTokensAsync(administratorId, id, cancellationToken),
                              result => result.Affected switch
                              {
                                  0 => _localiser["AdminUsers_NoTokensToRevoke"].Value,
                                  1 => _localiser["AdminUsers_RevokedOneToken"].Value,
                                  _ => _localiser["AdminUsers_RevokedTokens", result.Affected].Value,
                              },
                              cancellationToken);
    }

    public async Task<IActionResult> OnPostClearLockoutAsync(long id, CancellationToken cancellationToken)
    {
        return await ActAsync(id,
                              administratorId => _administration.ClearLockoutAsync(administratorId, id, cancellationToken),
                              _ => _localiser["AdminUsers_LockoutCleared"].Value,
                              cancellationToken);
    }

    /// <summary>
    /// Removes one of this account's passkeys - the recovery path for somebody whose device is gone,
    /// who then signs in with their password and enrols another.
    /// </summary>
    public async Task<IActionResult> OnPostRevokePasskeyAsync(long id, string? credentialId, CancellationToken cancellationToken)
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        HSUser? subject = await _users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture));

        if (subject is null)
        {
            return NotFound();
        }

        byte[] key;

        try
        {
            key = Base64Url.DecodeFromChars(credentialId ?? string.Empty);
        }
        catch (FormatException)
        {
            return NotFound();
        }

        if (await _users.GetPasskeyAsync(subject, key) is null)
        {
            StatusMessage = _localiser["Passkeys_Gone"].Value;

            return RedirectToPage(new { id });
        }

        await _users.RemovePasskeyAsync(subject, key);

        StatusMessage = _localiser["AdminPasskeys_Revoked"].Value;

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// The one shape every act on this page has: find the administrator, run the act, and say what
    /// happened - including why it was refused, which is a decision the service makes and this only
    /// puts into words.
    /// </summary>
    private async Task<IActionResult> ActAsync(long id,
                                               Func<long, Task<UserAdminResult>> act,
                                               Func<UserAdminResult, string> describe,
                                               CancellationToken cancellationToken)
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        UserAdminResult result = await act(administrator.Id);

        StatusMessage = result.Refusal switch
        {
            UserAdminRefusal.None => describe(result),
            UserAdminRefusal.Self => _localiser["AdminUsers_RefusedSelf"].Value,
            UserAdminRefusal.LastAdministrator => _localiser["AdminUsers_RefusedLastAdmin"].Value,
            _ => _localiser["AdminUsers_Gone"].Value,
        };

        return RedirectToPage(new { id });
    }

    /// <summary>Fills the page from the account, or answers false when there is no such account.</summary>
    private async Task<bool> LoadAsync(long id, CancellationToken cancellationToken)
    {
        HSUser? account = await _context.Users
                                        .AsNoTracking()
                                        .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

        if (account is null)
        {
            return false;
        }

        Id = account.Id;
        UserName = account.UserName ?? string.Empty;
        Email = account.Email;
        EmailConfirmed = account.EmailConfirmed;
        TwoFactorEnabled = account.TwoFactorEnabled;
        HasPassword = account.PasswordHash is not null;
        DeactivatedAt = account.DeactivatedAt;
        LockedOut = account.LockoutEnd > _time.GetUtcNow();
        IsSelf = _users.GetUserId(User) == account.Id.ToString(CultureInfo.InvariantCulture);

        IsAdministrator = await (from membership in _context.UserRoles
                                 join role in _context.Roles on membership.RoleId equals role.Id
                                 where role.Name == AdminBootstrap.AdminRole && membership.UserId == account.Id
                                 select membership.UserId)
                                .AnyAsync(cancellationToken);

        Passkeys =
        [
            .. (await _context.Set<IdentityUserPasskey<long>>()
                              .AsNoTracking()
                              .Where(passkey => passkey.UserId == account.Id)
                              .ToListAsync(cancellationToken))
               .Select(passkey => new PasskeyRow(Base64Url.EncodeToString(passkey.CredentialId),
                                                 passkey.Data.Name,
                                                 passkey.Data.CreatedAt,
                                                 passkey.Data.IsBackupEligible))
               .OrderBy(row => row.CreatedAt)
        ];

        Tokens =
        [
            .. (await _context.ApiTokens
                              .AsNoTracking()
                              .Where(token => token.UserId == account.Id)
                              .OrderByDescending(token => token.CreatedAt)
                              .ToListAsync(cancellationToken))
               .Select(token => new TokenRow(token.Name, _capabilities.Describe(token.Scope), token.CreatedAt))
        ];

        Teams =
        [
            .. (await _teams.GetTeamsForUserAsync(account.Id, cancellationToken))
               .Select(membership => new TeamRow(
                           membership.Team?.Name ?? _localiser["Common_TeamNumbered", membership.TeamId].Value,
                           _capabilities.Describe(membership.Capabilities),
                           membership.IsDefault))
        ];

        return true;
    }
}
