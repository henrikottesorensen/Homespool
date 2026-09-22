using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// What an administrator may do to somebody else's account: close it, reopen it, revoke its API
/// tokens or one of its passkeys, and lift the backoffs holding it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>One service behind both administration pages, because the guards must exist once.</b> Whether
/// an act is refused - your own account, the last administrator standing - is a property of the act
/// rather than of the button that asked for it, and a second page reimplementing either check is how
/// the two come to disagree.
/// </para>
/// <para>
/// <b>Every act first asks whether the administrator asking is still open.</b> A cookie outlives
/// its account's closure until the security stamp is next re-checked, so for that window a closed
/// administrator, the one an administrator closes for being compromised, still carries the role.
/// The administration pages' policy reads the row for the same reason; this class asks for itself
/// because a refusal is a property of the act, and a caller that is not the page gets it too. It
/// also makes a Self check on reopening
/// unnecessary: an open administrator aiming at their own account finds nothing to reopen, and a
/// closed one is refused before aiming. It also leaves the last-administrator refusal with no route
/// from the page - the one administrator left is refused as themselves, and the closed one who used
/// to reach it is refused sooner - so that guard now stands for a caller the page is not.
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
/// bump, and it matters more here. <c>UserManager.RemovePasskeyAsync</c> is the sharpest case: the
/// store saves the deletion before the validators run, so a refusal comes back as a failed result for
/// a passkey that is already gone, and the result says nothing about whether it was revoked.
/// </para>
/// </remarks>
public sealed class UserAdministration
{
    private readonly HomespoolDbContext _dbContext;
    private readonly ApiTokenService _tokens;
    private readonly AttemptLimiter _limiter;
    private readonly UnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly CredentialNotices _notices;
    private readonly ILogger<UserAdministration> _logger;

    public UserAdministration(HomespoolDbContext dbContext,
                              ApiTokenService tokens,
                              AttemptLimiter limiter,
                              UnitOfWork unitOfWork,
                              TimeProvider time,
                              CredentialNotices notices,
                              ILogger<UserAdministration> logger)
    {
        _dbContext = dbContext;
        _tokens = tokens;
        _limiter = limiter;
        _unitOfWork = unitOfWork;
        _time = time;
        _notices = notices;
        _logger = logger;
    }

    /// <summary>
    /// Closes <paramref name="userId"/>: no credential it holds is believed afterwards, and its API
    /// tokens are gone rather than merely refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The checks and the three writes are one serializable transaction.</b> The writes, because
    /// the half-done states are each worse than either end: an account marked closed whose tokens
    /// still authenticate, or tokens destroyed on an account that is still open. The checks, because
    /// the last-administrator answer is only true of the moment it is read: two administrators
    /// closing each other at once would each count two, each pass, and leave none - a state that
    /// keeps first-time setup closed and is repaired only by editing the database. Holding the write
    /// lock from the first read makes the second of them read the first's committed row instead.
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
        int revoked;

        // A refusal returns from inside the transaction; disposing it uncommitted writes nothing.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken))
        {
            if (await IsClosedAdministratorAsync(administratorId, cancellationToken))
            {
                return UserAdminResult.Refused(UserAdminRefusal.ClosedAdministrator);
            }

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
        if (await IsClosedAdministratorAsync(administratorId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.ClosedAdministrator);
        }

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
        if (await IsClosedAdministratorAsync(administratorId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.ClosedAdministrator);
        }

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
    /// Removes one of <paramref name="userId"/>'s passkeys, and says whether there was one to remove -
    /// the recovery path for somebody whose device is gone, who then signs in some other way and
    /// enrols another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The device's sessions end with it</b>, though not in this method: a session signed in with
    /// a passkey names it, and <see cref="SessionStampValidator"/> ends one whose passkey is no longer
    /// on the account when it next re-checks the cookie. The owner's other browsers are untouched.
    /// </para>
    /// <para>
    /// <b>Refused for your own account.</b> An administrator's own passkeys have their own page, which
    /// is where the owner of any account removes one; this screen is for the case where the owner
    /// cannot.
    /// </para>
    /// <para>
    /// <b>The delete's row count is the answer</b>, not a lookup before it, so a passkey removed by
    /// somebody else in between reads as already gone rather than as revoked twice. The name is read
    /// first only for the log line. The owner is mailed only when a row went.
    /// </para>
    /// </remarks>
    public async Task<UserAdminResult> RevokePasskeyAsync(long administratorId,
                                                          long userId,
                                                          byte[] credentialId,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);

        if (await IsClosedAdministratorAsync(administratorId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.ClosedAdministrator);
        }

        HSUser? user = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return UserAdminResult.Refused(UserAdminRefusal.NoSuchAccount);
        }

        if (userId == administratorId)
        {
            return UserAdminResult.Refused(UserAdminRefusal.Self);
        }

        IQueryable<IdentityUserPasskey<long>> passkey = _dbContext.Set<IdentityUserPasskey<long>>()
                                                                  .Where(p => p.UserId == userId && p.CredentialId == credentialId);

        string? name = (await passkey.AsNoTracking().SingleOrDefaultAsync(cancellationToken))?.Data.Name;

        int revoked = await passkey.ExecuteDeleteAsync(cancellationToken);

        if (revoked > 0)
        {
            _logger.LogWarning("Administrator {AdministratorId} revoked passkey {PasskeyName} of user {UserId}.",
                               administratorId,
                               LogText.Clean(name),
                               userId);

            await _notices.TellAsync(user, CredentialChange.PasskeyRevoked);
        }

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
        if (await IsClosedAdministratorAsync(administratorId, cancellationToken))
        {
            return UserAdminResult.Refused(UserAdminRefusal.ClosedAdministrator);
        }

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
    /// Whether the account asking, <paramref name="administratorId"/>, is closed - or is no account at
    /// all, which is refused the same way rather than trusted.
    /// </summary>
    /// <remarks>
    /// Read from the row on every act, not from the caller's claims: the claims are what a closed
    /// administrator's cookie still carries until its stamp is next re-checked.
    /// </remarks>
    private async Task<bool> IsClosedAdministratorAsync(long administratorId, CancellationToken cancellationToken)
    {
        return !await _dbContext.Users.AnyAsync(u => u.Id == administratorId && u.DeactivatedAt == null, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is an administrator and the only one still active.
    /// </summary>
    /// <remarks>
    /// One query for both halves, and it is <see cref="Administrators.Open"/> - the definition every
    /// other administrator decision asks - so the count and the "is this one of them" test cannot
    /// answer from different pictures of the same table, or from a different idea of who counts.
    /// </remarks>
    private async Task<bool> IsLastActiveAdministratorAsync(long userId, CancellationToken cancellationToken)
    {
        long[] activeAdministrators = await Administrators.Open(_dbContext).ToArrayAsync(cancellationToken);

        return activeAdministrators.Contains(userId) && activeAdministrators.Length == 1;
    }
}
