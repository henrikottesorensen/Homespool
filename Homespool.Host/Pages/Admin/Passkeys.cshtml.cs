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
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// Every passkey enrolled on this deployment, and the one thing an administrator does with one:
/// revoke it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the recovery path.</b> A person who loses the device a passkey lives on asks the
/// administrator, who removes it here; they sign in with their password and add another. Out of band
/// and human-verified, which on a household appliance beats any automated reset. Without this screen
/// a lost passkey would be a credential nobody could remove short of editing the database.
/// </para>
/// <para>
/// <b>Listing and revoking is all it does.</b> It is not a user manager, and its name says so; a
/// screen that promised one would grow one behind it.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class PasskeysModel : PageModel
{
    private readonly HomespoolDbContext _context;
    private readonly UserManager<HSUser> _users;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<PasskeysModel> _logger;

    public PasskeysModel(HomespoolDbContext context,
                         UserManager<HSUser> users,
                         IStringLocalizer<SharedResource> localiser,
                         ILogger<PasskeysModel> logger)
    {
        _context = context;
        _users = users;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>One enrolled passkey, with the account it belongs to.</summary>
    public sealed record Row(long UserId, string UserName, string Id, string? Name, DateTimeOffset CreatedAt, bool Synced);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Read whole and shaped in memory: the credential record is one JSON column, so nothing in it
        // is queryable, and at one-to-tens-of-users nothing needs to be.
        List<IdentityUserPasskey<long>> passkeys = await _context.Set<IdentityUserPasskey<long>>()
                                                                 .AsNoTracking()
                                                                 .ToListAsync(cancellationToken);

        List<long> owners = passkeys.Select(p => p.UserId).Distinct().ToList();

        Dictionary<long, string> names = await _context.Users
                                                       .Where(u => owners.Contains(u.Id))
                                                       .ToDictionaryAsync(u => u.Id, u => u.UserName ?? string.Empty, cancellationToken);

        Rows =
        [
            .. passkeys.Select(p => new Row(
                                   p.UserId,
                                   names.GetValueOrDefault(p.UserId, p.UserId.ToString(CultureInfo.InvariantCulture)),
                                   Base64Url.EncodeToString(p.CredentialId),
                                   p.Data.Name,
                                   p.Data.CreatedAt,
                                   p.Data.IsBackupEligible))
                       .OrderBy(r => r.UserName, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(r => r.CreatedAt)
        ];
    }

    public async Task<IActionResult> OnPostRevokeAsync(long userId, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return NotFound();
        }

        byte[] credentialId;

        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return NotFound();
        }

        HSUser? owner = await _users.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        UserPasskeyInfo? passkey = owner is null ? null : await _users.GetPasskeyAsync(owner, credentialId);

        if (owner is null || passkey is null)
        {
            StatusMessage = _localiser["Passkeys_Gone"];

            return RedirectToPage();
        }

        await _users.RemovePasskeyAsync(owner, credentialId);

        // Warning rather than information: somebody's credential was removed by somebody else, which
        // is the kind of line an operator reads back later.
        _logger.LogWarning("Administrator {AdminId} revoked passkey {PasskeyName} of user {UserId}.",
                           _users.GetUserId(User),
                           passkey.Name,
                           owner.Id);

        StatusMessage = _localiser["AdminPasskeys_Revoked"];

        return RedirectToPage();
    }
}
