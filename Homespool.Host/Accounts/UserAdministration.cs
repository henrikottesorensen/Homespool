using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// What an administrator may do to somebody else's account: close it, reopen it, revoke its API
/// tokens, and lift the backoffs holding it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>One service behind both administration pages, because the guards must exist once.</b> Whether
/// an act is refused - your own account, the last administrator standing - is a property of the act
/// rather than of the button that asked for it, and a second page reimplementing either check is how
/// the two come to disagree.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> No hard delete: attribution is history, and deleting the
/// subject of a record makes the record lie. No administrator-set password, which would be a second
/// credential on an account its owner does not know about. No impersonation, which would make every
/// record of who did what unreliable. Each of those is a thing this screen could plausibly grow, and
/// each is refused on its own reasoning rather than for want of time.
/// </para>
/// <para>
/// <b>Every write in this class goes through the context rather than <c>UserManager</c>.</b> The
/// manager's update path runs the user validators, so closing a compromised account could fail
/// because its username stopped validating against somebody else's - which is exactly the moment the
/// act must not fail. The same reasoning <see cref="AttemptLimiter"/> already applies to a counter
/// bump, and it matters more here.
/// </para>
/// <para>
/// <b>"In this class" is load-bearing.</b> Not every administrative act is routed through here:
/// <c>Admin/Users/Detail</c>'s passkey revoke calls <c>UserManager.RemovePasskeyAsync</c> directly,
/// so it runs the validators this class exists to avoid - and it discards the <c>IdentityResult</c>,
/// reporting the revoke as done whether or not it happened. An act that must not fail, or must not
/// fail quietly, belongs here rather than on a page.
/// </para>
/// </remarks>
public sealed class UserAdministration
{
    private readonly HomespoolDbContext _dbContext;
    private readonly ApiTokenService _tokens;
    private readonly AttemptLimiter _limiter;
    private readonly UnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger<UserAdministration> _logger;

    public UserAdministration(HomespoolDbContext dbContext,
                              ApiTokenService tokens,
                              AttemptLimiter limiter,
                              UnitOfWork unitOfWork,
                              TimeProvider time,
                              ILogger<UserAdministration> logger)
    {
        _dbContext = dbContext;
        _tokens = tokens;
        _limiter = limiter;
        _unitOfWork = unitOfWork;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Closes <paramref name="userId"/>: no credential it holds is believed afterwards, and its API
    /// tokens are gone rather than merely refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The three writes are one transaction</b>, because the half-done states are each worse than
    /// either end: an account marked closed whose tokens still authenticate, or tokens destroyed on
    /// an account that is still open.
    /// </para>
    /// <para>
    /// <b>The tokens are deleted, not left to the sign-in check that would now refuse them.</b> The
    /// check is what makes the closure immediate; deleting is what makes it survive a reopening, so
    /// that reactivating an account does not silently hand a compromise back its credentials.
    /// </para>
    /// <para>
    /// <b>The security stamp moves, which ends the account's browser sessions</b> - though not at
    /// once: a session cookie is re-checked against the stamp on
    /// <c>SecurityStampValidatorOptions.ValidationInterval</c>, so a signed-in browser keeps working
    /// until that falls due. The tokens, which are read from the database on every request, stop
    /// instantly.
    /// </para>
    /// </remarks>
    /// <param name="administratorId">Who is doing this, for the log and for the self check.</param>
    /// <param name="userId">The account to close.</param>
    /// <param name="cancellationToken">Cancels the writes; nothing lands if it fires first.</param>
    public async Task<UserAdminResult> DeactivateAsync(long administratorId,
                                                       long userId,
                                                       CancellationToken cancellationToken)
    {
        HSUser? user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return UserAdminResult.Refused(UserAdminRefusal.NoSuchAccount);
        }

        if (userId == administratorId)
        {
            return UserAdminResult.Refused(UserAdminRefusal.Self);
        }

        if (await IsLastActiveAdministratorAsync(userId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.LastAdministrator);
        }

        if (user.DeactivatedAt is not null)
        {
            // Already closed. Reporting this as done rather than as a refusal keeps a double
            // submission from reading like a failure, and there is nothing left to do either way.
            return UserAdminResult.Done();
        }

        int revoked;

        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            user.DeactivatedAt = _time.GetUtcNow();

            // A fresh stamp is what invalidates the cookies already issued. Assigned rather than
            // taken from UserManager.UpdateSecurityStampAsync, which would run the validators this
            // class avoids; the value's shape is the entity's own, from its constructor.
            user.SecurityStamp = Guid.NewGuid().ToString();

            await _dbContext.SaveChangesAsync(cancellationToken);

            revoked = await _tokens.RevokeAllForUserAsync(userId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogWarning(
            "Administrator {AdministratorId} deactivated user {UserId}; {RevokedTokenCount} API tokens revoked.",
            administratorId,
            userId,
            revoked);

        return UserAdminResult.Done(revoked);
    }

    /// <summary>
    /// Reopens <paramref name="userId"/>. Its credentials work again - except the API tokens, which
    /// were deleted rather than suspended and have to be minted afresh by their owner.
    /// </summary>
    public async Task<UserAdminResult> ReactivateAsync(long administratorId,
                                                       long userId,
                                                       CancellationToken cancellationToken)
    {
        HSUser? user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return UserAdminResult.Refused(UserAdminRefusal.NoSuchAccount);
        }

        if (user.DeactivatedAt is null)
        {
            return UserAdminResult.Done();
        }

        // The stamp is left alone: it moved when the account was closed, which already ended every
        // session it had. Moving it again would end nothing and would invalidate the pending
        // email-confirmation and password-reset tokens the person may be holding.
        user.DeactivatedAt = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Administrator {AdministratorId} reactivated user {UserId}.", administratorId, userId);

        return UserAdminResult.Done();
    }

    /// <summary>
    /// Revokes every API token <paramref name="userId"/> holds, and says how many there were.
    /// </summary>
    /// <remarks>
    /// <b>The only way anybody but the owner can stop a token.</b> A token carries no expiry by
    /// design, so without this the answer to "somebody's laptop was stolen" was to hope they still
    /// had a session to revoke from. Deleting the row is what revocation is here.
    /// </remarks>
    public async Task<UserAdminResult> RevokeTokensAsync(long administratorId,
                                                         long userId,
                                                         CancellationToken cancellationToken)
    {
        if (!await _dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.NoSuchAccount);
        }

        int revoked = await _tokens.RevokeAllForUserAsync(userId, cancellationToken);

        _logger.LogWarning("Administrator {AdministratorId} revoked {RevokedTokenCount} API tokens of user {UserId}.",
                           administratorId,
                           revoked,
                           userId);

        return UserAdminResult.Done(revoked);
    }

    /// <summary>
    /// Lifts everything holding <paramref name="userId"/> out: the account lockout that failed
    /// sign-ins built up, and every <see cref="AttemptLimiter"/> backoff on the account.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because either alone leaves somebody stuck.</b> The account lockout is what a
    /// wrong password builds; the backoffs are what a flood of password-reset mail builds, and that
    /// one is spent by whoever knows the address rather than by the account's owner. An operator
    /// clearing "the lockout" means the person can get back in, and that takes both.
    /// </remarks>
    public async Task<UserAdminResult> ClearLockoutAsync(long administratorId,
                                                         long userId,
                                                         CancellationToken cancellationToken)
    {
        HSUser? user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return UserAdminResult.Refused(UserAdminRefusal.NoSuchAccount);
        }

        int cleared;

        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;

            await _dbContext.SaveChangesAsync(cancellationToken);

            cleared = await _limiter.ResetAllAsync(userId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogWarning("Administrator {AdministratorId} cleared the lockout of user {UserId}; {BackoffCount} backoffs lifted.",
                           administratorId,
                           userId,
                           cleared);

        return UserAdminResult.Done(cleared);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is an administrator and the only one still active.
    /// </summary>
    /// <remarks>
    /// One query for both halves. It reads the role membership rather than asking
    /// <c>UserManager</c>, so the count and the "is this one of them" test cannot answer from
    /// different pictures of the same table.
    /// </remarks>
    private async Task<bool> IsLastActiveAdministratorAsync(long userId, CancellationToken cancellationToken)
    {
        long[] activeAdministrators = await (from membership in _dbContext.UserRoles
                                             join role in _dbContext.Roles on membership.RoleId equals role.Id
                                             join account in _dbContext.Users on membership.UserId equals account.Id
                                             where role.Name == AdminBootstrap.AdminRole && account.DeactivatedAt == null
                                             select account.Id)
                                            .Distinct()
                                            .ToArrayAsync(cancellationToken);

        return activeAdministrators.Contains(userId) && activeAdministrators.Length == 1;
    }
}
