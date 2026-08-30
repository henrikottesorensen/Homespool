using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;

using Homespool.Host.Authorisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// The printer an account reaches for when a page has to pick one, and the one place that decides
/// whether a stored choice still counts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading is a question about now, not about what was stored.</b>
/// <see cref="HSUser.DefaultPrinterId"/> is a plain id with no foreign key behind it, so it can name
/// a printer that has been removed, or one on a team the account has since been dropped from.
/// Resolving asks whether the caller may still view it and answers null when they may not, which is
/// what lets removal and team changes leave this column alone.
/// </para>
/// <para>
/// <b><see cref="Capability.ViewPrinter"/> is the whole gate, deliberately.</b> Naming a printer as
/// your default changes nothing about the printer - it is a preference belonging to the account, and
/// the printers it may choose between are the printers it can already see. The capabilities that
/// decide what may then be <em>done</em> to it are asked where that happens.
/// </para>
/// </remarks>
public class DefaultPrinterService
{
    private readonly PrinterAccessService _access;
    private readonly UserManager<HSUser> _userManager;

    public DefaultPrinterService(PrinterAccessService access, UserManager<HSUser> userManager)
    {
        _access = access;
        _userManager = userManager;
    }

    /// <summary>
    /// The caller's default printer id, or null when none is stored or the stored one is no longer
    /// theirs to see.
    /// </summary>
    public async Task<int?> ResolveAsync(HSUser user, Caller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.DefaultPrinterId is not int printerId)
        {
            return null;
        }

        return await _access.AllowsAsync(printerId, caller, Capability.ViewPrinter, cancellationToken) ?
            printerId :
            null;
    }

    /// <summary>
    /// Makes <paramref name="printerId"/> the caller's default, or refuses if they may not see it.
    /// </summary>
    /// <returns>Whether the choice was stored.</returns>
    /// <remarks>
    /// <b>The id is checked here rather than trusted from the form.</b> Every caller reaches this
    /// from a page that resolved the printer already, so the check is a second one - and it is the
    /// one that would still hold if a later caller did not, which is the point of it living in the
    /// service rather than in each handler.
    /// </remarks>
    public async Task<bool> SetAsync(HSUser user, Caller caller, int printerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!await _access.AllowsAsync(printerId, caller, Capability.ViewPrinter, cancellationToken))
        {
            return false;
        }

        return await StoreAsync(user, printerId);
    }

    /// <summary>
    /// Leaves the caller with no default printer.
    /// </summary>
    /// <remarks>
    /// Unconditional: clearing needs no permission on a printer, and a stored id the caller can no
    /// longer see is exactly the one they should still be able to get rid of.
    /// </remarks>
    public Task<bool> ClearAsync(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return StoreAsync(user, null);
    }

    /// <summary>
    /// Writes the column through Identity, so the account's own concurrency stamp governs it.
    /// </summary>
    /// <remarks>
    /// No <c>RefreshSignInAsync</c>: nothing about this reaches a claim, so the cookie has nothing to
    /// go stale about and the next request reads the column.
    /// </remarks>
    private async Task<bool> StoreAsync(HSUser user, int? printerId)
    {
        user.DefaultPrinterId = printerId;

        IdentityResult result = await _userManager.UpdateAsync(user);

        return result.Succeeded;
    }
}
