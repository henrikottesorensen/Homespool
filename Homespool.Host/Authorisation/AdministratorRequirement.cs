using System.Globalization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// An administrator whose account is still open, read from the row rather than from the cookie.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the role claim is not enough.</b> A cookie carries the role it was issued with until the
/// security stamp is next re-checked, five minutes at most, so for that window an administrator
/// closed as compromised still satisfies <c>[Authorize(Roles = …)]</c> on every administration page.
/// This requirement costs one indexed read per request to those pages, which are a handful a day;
/// the same read on every authenticated page was refused on cost, and this does not reopen that.
/// </para>
/// <para>
/// <b>The role is still read from the cookie first</b>, and a principal without it never reaches
/// the database: the read is what a closed administrator costs, not what every visitor does.
/// </para>
/// </remarks>
public sealed class AdministratorRequirement : IAuthorizationRequirement
{
}

/// <summary>Satisfies <see cref="AdministratorRequirement"/> from the account's row.</summary>
public sealed class AdministratorHandler : AuthorizationHandler<AdministratorRequirement>
{
    private readonly HomespoolDbContext _dbContext;
    private readonly UserManager<HSUser> _users;

    public AdministratorHandler(HomespoolDbContext dbContext, UserManager<HSUser> users)
    {
        _dbContext = dbContext;
        _users = users;
    }

    /// <inheritdoc/>
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                         AdministratorRequirement requirement)
    {
        if (!context.User.IsInRole(AdminBootstrap.AdminRole) ||
            !long.TryParse(_users.GetUserId(context.User), CultureInfo.InvariantCulture, out long id))
        {
            return;
        }

        if (await _dbContext.Users.AnyAsync(u => u.Id == id && u.DeactivatedAt == null))
        {
            context.Succeed(requirement);
        }
        else
        {
            // Fail rather than merely not succeed: no other handler may vouch for a closed account.
            context.Fail(new AuthorizationFailureReason(this, "The administrator's account is closed."));
        }
    }
}
