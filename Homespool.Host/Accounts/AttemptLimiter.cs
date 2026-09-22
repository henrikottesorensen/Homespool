using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Bounds how fast one account can guess at one <see cref="LimitedAction"/>, with an exponential
/// backoff that always self-heals - or, for an action that is not a guess, holds it to a fixed
/// cooldown after each use.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes a short secret safe on an <i>authenticated</i> path.</b> Anonymous
/// endpoints sit behind a global limiter; a signed-in page has none of its own, so without this an
/// account could grind a code space at request rate. Two such secrets exist - a registration code on
/// the claim page, and an authenticator code confirming a printer's removal.
/// </para>
/// <para>
/// <b>Backoff, never invalidation.</b> Burning the thing being guessed at after N wrong tries would
/// turn the cap into a denial of service against its owner. Backing the <i>caller</i> off instead
/// leaves the secret untouched.
/// </para>
/// <para>
/// <b>Was <c>ClaimAttemptLimiter</c>, keyed to one action and stored in two columns on
/// <c>HSUser</c>.</b> Generalised 2026-08-26 when a second action needed the same treatment: a
/// second pair of columns would have become a third. The arithmetic is unchanged, which is what
/// <c>AttemptLimiterTests</c> pins.
/// </para>
/// <para>
/// Writes through <see cref="HomespoolDbContext"/> rather than <c>UserManager.UpdateAsync</c>
/// deliberately: that would run the user validators and save on its own schedule, and a counter bump
/// has no business failing because some unrelated field stopped validating.
/// </para>
/// </remarks>
public class AttemptLimiter
{
    private readonly HomespoolDbContext _dbContext;
    private readonly AttemptLimitOptions _options;
    private readonly ILogger<AttemptLimiter> _logger;

    public AttemptLimiter(HomespoolDbContext dbContext,
                          IOptionsSnapshot<AttemptLimitOptions> options,
                          ILogger<AttemptLimiter> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// How much longer this account is backed off from <paramref name="action"/>, or null if it may
    /// attempt it now.
    /// </summary>
    /// <remarks>
    /// Untracked: this is a read, and tracking it would let a later <c>SaveChanges</c> in the same
    /// request write back a row this never meant to modify.
    /// </remarks>
    /// <param name="userId">The signed-in account.</param>
    /// <param name="action">Which action is being attempted.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<TimeSpan?> RemainingLockoutAsync(long userId,
                                                       LimitedAction action,
                                                       DateTimeOffset now,
                                                       CancellationToken cancellationToken)
    {
        DateTimeOffset? lockoutEnd = await _dbContext.UserActionAttempts
                                                     .AsNoTracking()
                                                     .Where(a => a.UserId == userId && a.Action == action)
                                                     .Select(a => a.LockoutEnd)
                                                     .SingleOrDefaultAsync(cancellationToken);

        if (lockoutEnd is not { } end || end <= now)
        {
            return null;
        }

        return end - now;
    }

    /// <summary>
    /// Records a failed attempt, backing the account off once
    /// <see cref="AttemptLimitOptions.MaxFailedAttempts"/> is passed.
    /// </summary>
    /// <remarks>
    /// <b>Saves on its own</b>, because a caller may be recording this after its own transaction has
    /// rolled back - and a failure counted inside that transaction would roll back with it, which is
    /// precisely the "rollback undoes the thing you were counting on" trap.
    /// </remarks>
    /// <param name="userId">The account whose attempt failed.</param>
    /// <param name="action">Which action was attempted.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task RecordFailedAttemptAsync(long userId,
                                               LimitedAction action,
                                               DateTimeOffset now,
                                               CancellationToken cancellationToken)
    {
        UserActionAttempt? attempt = await _dbContext.UserActionAttempts
                                                     .SingleOrDefaultAsync(
                                                         a => a.UserId == userId && a.Action == action,
                                                         cancellationToken);

        if (attempt is null)
        {
            attempt = new UserActionAttempt { UserId = userId, Action = action };
            _dbContext.UserActionAttempts.Add(attempt);
        }

        attempt.FailedCount++;

        int over = attempt.FailedCount - _options.MaxFailedAttempts;

        if (over > 0)
        {
            // Doubling from the base on each failure past the threshold, capped. Shifting is done on
            // a long and clamped before it reaches the timestamp, so a misconfigured base cannot
            // overflow into a negative - which would read as "not locked out" and silently disable
            // the cap.
            int doublings = Math.Min(over - 1, 30);
            long seconds = Math.Min((long)_options.LockoutBaseSeconds << doublings,
                                    _options.LockoutMaxSeconds);

            attempt.LockoutEnd = now.AddSeconds(seconds);

            _logger.LogWarning("User {UserId} backed off from {Action} for {LockoutSeconds}s after " +
                               "{FailedAttempts} failed attempts.",
                               userId, action, seconds, attempt.FailedCount);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Holds this account off <paramref name="action"/> for <paramref name="duration"/> from
    /// <paramref name="now"/>, whatever it did before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fixed wait after every use, for an action that is not a guess.</b> Nothing is counted and
    /// the wait never grows, so the owner is never held off for longer than <paramref name="duration"/>;
    /// <see cref="RemainingLockoutAsync"/> reads it like any backoff, and
    /// <see cref="ResetAllAsync"/> clears it with the rest.
    /// </para>
    /// <para>
    /// <b>For a cooldown-only action.</b> It overwrites the lockout end, so on an action whose
    /// failures are also counted it could shorten a longer backoff already running.
    /// </para>
    /// <para>
    /// Saves on its own, like <see cref="RecordFailedAttemptAsync"/>: the caller starts the cooldown
    /// before doing the thing it bounds, and whether that then succeeds does not return the use.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account that just used the action.</param>
    /// <param name="action">Which action was used.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="duration">How long the account must wait before using it again.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task StartCooldownAsync(long userId,
                                         LimitedAction action,
                                         DateTimeOffset now,
                                         TimeSpan duration,
                                         CancellationToken cancellationToken)
    {
        UserActionAttempt? attempt = await _dbContext.UserActionAttempts
                                                     .SingleOrDefaultAsync(
                                                         a => a.UserId == userId && a.Action == action,
                                                         cancellationToken);

        if (attempt is null)
        {
            attempt = new UserActionAttempt { UserId = userId, Action = action };
            _dbContext.UserActionAttempts.Add(attempt);
        }

        attempt.LockoutEnd = now.Add(duration);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Clears the failure count after a success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deletes the row rather than zeroing it</b>, so an account that has recovered leaves no
    /// trace and the table holds only accounts currently getting something wrong.
    /// </para>
    /// <para>
    /// Saves through the same request-scoped <see cref="HomespoolDbContext"/> the caller's
    /// transaction is ambient over, so calling this inside one enrols the reset in it and a rollback
    /// takes the reset with it - which is what makes "the counter is cleared only if the action
    /// actually landed" true rather than merely intended.
    /// </para>
    /// <para>
    /// A no-op when there is nothing to clear, which is the overwhelmingly common case: it keeps an
    /// ordinary first-try success from writing anything at all.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account whose attempt succeeded.</param>
    /// <param name="action">Which action succeeded.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task ResetAsync(long userId, LimitedAction action, CancellationToken cancellationToken)
    {
        UserActionAttempt? attempt = await _dbContext.UserActionAttempts
                                                     .SingleOrDefaultAsync(
                                                         a => a.UserId == userId && a.Action == action,
                                                         cancellationToken);

        if (attempt is null)
        {
            return;
        }

        _dbContext.UserActionAttempts.Remove(attempt);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Clears every backoff this account is under, whatever the action, and says how many there were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For an administrator unsticking an account, and nothing else.</b> Every other caller resets
    /// the one action it just saw succeed - which is what keeps a backoff meaningful. This is the
    /// operator's override, for an account whose owner cannot wait out a backoff that has grown to its
    /// cap. The cooldowns go with it, which costs nothing: each is short and ends on its own.
    /// </para>
    /// <para>
    /// Bulk and untracked, like <c>ApiTokenService.RevokeAllForUserAsync</c>: there is nothing to
    /// load, and it still joins an ambient transaction, which is what lets the caller clear these and
    /// the account lockout as one act.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account to clear.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    public async Task<int> ResetAllAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.UserActionAttempts
                               .Where(a => a.UserId == userId)
                               .ExecuteDeleteAsync(cancellationToken);
    }
}
