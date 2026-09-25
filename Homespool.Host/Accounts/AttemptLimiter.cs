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
/// <b>Every write that decides a backoff is a compare-and-swap.</b> Loading the row, changing it in
/// memory and saving it lets concurrent requests overwrite each other's counts, and lets every one of
/// them pass a check none of the others has written yet: eight parallel failures were measured leaving
/// a count of one or two. Here the row is read, the next count and backoff are worked out, and the
/// update applies only where the row still holds what was read; a request that loses re-reads and
/// tries again, and sees the backoff the winner set. The first row is inserted, and an insert that
/// loses to a parallel one on the unique index goes round as an update.
/// </para>
/// <para>
/// Writes through <see cref="HomespoolDbContext"/> rather than <c>UserManager.UpdateAsync</c>
/// deliberately: that would run the user validators and save on its own schedule, and a counter bump
/// has no business failing because some unrelated field stopped validating.
/// </para>
/// </remarks>
public class AttemptLimiter
{
    /// <summary>
    /// How many times a count or a cooldown is re-read after losing to a parallel write before it
    /// gives up. A take stops losing once the backoff its rivals set is in force, so it needs at most
    /// <see cref="AttemptLimitOptions.MaxFailedAttempts"/> plus one rounds; this is a ceiling on a
    /// burst of unconditional counts, never reached by a caller comparing a secret.
    /// </summary>
    private const int MaxPasses = 64;

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
    /// <para>
    /// Untracked: this is a read, and tracking it would let a later <c>SaveChanges</c> in the same
    /// request write back a row this never meant to modify.
    /// </para>
    /// <para>
    /// <b>A read, not a reservation</b>: parallel callers all see the same answer. A caller about to
    /// compare a secret takes the attempt with <see cref="TakeAttemptAsync"/>, which checks and counts
    /// as one act; this is for saying how long, and for a check before something that is not counted.
    /// </para>
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
        DateTimeOffset? lockoutEnd = await Rows(userId, action).AsNoTracking()
                                                                .Select(a => a.LockoutEnd)
                                                                .SingleOrDefaultAsync(cancellationToken);

        if (lockoutEnd is not { } end || end <= now)
        {
            return null;
        }

        return end - now;
    }

    /// <summary>
    /// Counts an attempt at <paramref name="action"/> as a failure before its secret is compared -
    /// unless the account is backed off, when nothing is counted and the ticket says for how long.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The check and the count are one act</b>, so a burst cannot all pass the check before any of
    /// it is counted: the count only lands on the row the check read, and the attempt that crosses the
    /// threshold sets the backoff the next one is refused by. A right answer then gives the attempt
    /// back with <see cref="ResetAsync"/>, and an outcome that was not a guess with
    /// <see cref="ReturnAttemptAsync"/>; a wrong one needs nothing further.
    /// </para>
    /// <para>
    /// <b>Refused inside a transaction.</b> A count written in the caller's transaction is undone by
    /// its rollback, which turns every wrong guess that fails the surrounding work into a free one.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account attempting.</param>
    /// <param name="action">Which action is being attempted.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<AttemptTicket> TakeAttemptAsync(long userId,
                                                      LimitedAction action,
                                                      DateTimeOffset now,
                                                      CancellationToken cancellationToken)
    {
        if (await CountAsync(userId, action, now, onlyIfNotBackedOff: true, cancellationToken) is { } counted)
        {
            return AttemptTicket.Counted(counted.failedCount > _options.MaxFailedAttempts ? counted.lockoutEnd : null);
        }

        return AttemptTicket.Refused(await RemainingLockoutAsync(userId, action, now, cancellationToken) ?? TimeSpan.Zero);
    }

    /// <summary>
    /// Records a failed attempt, backing the account off once
    /// <see cref="AttemptLimitOptions.MaxFailedAttempts"/> is passed - whether or not it is backed off
    /// already. A caller comparing a secret wants <see cref="TakeAttemptAsync"/>, which counts first.
    /// </summary>
    /// <remarks>
    /// <b>Saves on its own, and refuses to run inside a transaction</b>: a failure counted inside one
    /// would roll back with it, which is precisely the "rollback undoes the thing you were counting on"
    /// trap.
    /// </remarks>
    /// <param name="userId">The account whose attempt failed.</param>
    /// <param name="action">Which action was attempted.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <exception cref="InvalidOperationException">Parallel writes kept the count from landing.</exception>
    public async Task RecordFailedAttemptAsync(long userId,
                                               LimitedAction action,
                                               DateTimeOffset now,
                                               CancellationToken cancellationToken)
    {
        if (await CountAsync(userId, action, now, onlyIfNotBackedOff: false, cancellationToken) is null)
        {
            throw new InvalidOperationException($"A failed {action} for user {userId} could not be counted: parallel writes kept changing the row.");
        }
    }

    /// <summary>
    /// Gives back an attempt <see cref="TakeAttemptAsync"/> counted whose outcome turned out not to be
    /// a guess - a right code refused for another reason - and lifts the backoff it imposed.
    /// </summary>
    /// <remarks>
    /// Removes the row when the attempt was all it held, as <see cref="ResetAsync"/> would, so a
    /// returned attempt leaves no trace. A backoff somebody else's attempt set stays.
    /// </remarks>
    /// <param name="userId">The account that attempted.</param>
    /// <param name="action">Which action was attempted.</param>
    /// <param name="attempt">The ticket <see cref="TakeAttemptAsync"/> gave.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task ReturnAttemptAsync(long userId,
                                         LimitedAction action,
                                         AttemptTicket attempt,
                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (!attempt.Taken)
        {
            return;
        }

        DateTimeOffset? imposed = attempt.LockoutImposed;

        int removed = await Rows(userId, action).Where(a => a.FailedCount <= 1 && (a.LockoutEnd == null || a.LockoutEnd == imposed))
                                                .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
        {
            return;
        }

        await Rows(userId, action).Where(a => a.FailedCount > 0)
                                  .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.FailedCount, a => a.FailedCount - 1)
                                                                        .SetProperty(a => a.LockoutEnd,
                                                                                     a => a.LockoutEnd == imposed ? null : a.LockoutEnd),
                                                      cancellationToken);
    }

    /// <summary>
    /// Holds this account off <paramref name="action"/> for <paramref name="duration"/> from
    /// <paramref name="now"/>, unless it is held off already: <see langword="null"/> when the cooldown
    /// started, otherwise how much longer the running one lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fixed wait after every use, for an action that is not a guess.</b> Nothing is counted and
    /// the wait never grows, so the owner is never held off for longer than <paramref name="duration"/>;
    /// <see cref="RemainingLockoutAsync"/> reads it like any backoff, and
    /// <see cref="ResetAllAsync"/> clears it with the rest.
    /// </para>
    /// <para>
    /// <b>The check and the start are one act</b>: the cooldown is written only over the lockout end
    /// the check read, so of parallel uses exactly one starts it and goes ahead. A refused use does not
    /// restart it: polling must not hold the door shut.
    /// </para>
    /// <para>
    /// <b>For a cooldown-only action.</b> It overwrites the lockout end, so on an action whose
    /// failures are also counted it could shorten a longer backoff already running.
    /// </para>
    /// <para>
    /// Saves on its own, and refuses to run inside a transaction: the caller starts the cooldown
    /// before doing the thing it bounds, and whether that then succeeds does not return the use.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account using the action.</param>
    /// <param name="action">Which action is being used.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="duration">How long the account must wait before using it again.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task<TimeSpan?> TryStartCooldownAsync(long userId,
                                                       LimitedAction action,
                                                       DateTimeOffset now,
                                                       TimeSpan duration,
                                                       CancellationToken cancellationToken)
    {
        OutsideTransaction.Require(_dbContext);

        DateTimeOffset end = now.Add(duration);

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var current = await Rows(userId, action).AsNoTracking()
                                                    .Select(a => new { a.LockoutEnd })
                                                    .SingleOrDefaultAsync(cancellationToken);

            if (current?.LockoutEnd is { } running && running > now)
            {
                return running - now;
            }

            bool started = current is null ?
                await TryInsertAsync(userId, action, failedCount: 0, end, cancellationToken) :
                await Rows(userId, action).Where(a => a.LockoutEnd == current.LockoutEnd)
                                          .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.LockoutEnd, end), cancellationToken) > 0;

            if (started)
            {
                return null;
            }
        }

        // Parallel writes kept changing the row. Refusing is the answer that fails safe: the use waits.
        return await RemainingLockoutAsync(userId, action, now, cancellationToken) ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Clears the failure count after a success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deletes the row rather than zeroing it</b>, so an account that has recovered leaves no
    /// trace and the table holds only accounts currently getting something wrong. The attempt
    /// <see cref="TakeAttemptAsync"/> counted for this success goes with it.
    /// </para>
    /// <para>
    /// One statement, in the request-scoped <see cref="HomespoolDbContext"/>'s transaction when the
    /// caller has one open, so a rollback takes the reset with it - which is what makes "the counter is
    /// cleared only if the action actually landed" true rather than merely intended.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account whose attempt succeeded.</param>
    /// <param name="action">Which action succeeded.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public Task ResetAsync(long userId, LimitedAction action, CancellationToken cancellationToken)
    {
        return Rows(userId, action).ExecuteDeleteAsync(cancellationToken);
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

    private IQueryable<UserActionAttempt> Rows(long userId, LimitedAction action)
    {
        return _dbContext.UserActionAttempts.Where(a => a.UserId == userId && a.Action == action);
    }

    /// <summary>
    /// Counts one failure and returns the count and backoff end it produced, or <see langword="null"/>
    /// when <paramref name="onlyIfNotBackedOff"/> held it back - or when parallel writes kept it from
    /// landing for <see cref="MaxPasses"/> rounds.
    /// </summary>
    private async Task<(int failedCount, DateTimeOffset? lockoutEnd)?> CountAsync(long userId,
                                                                                  LimitedAction action,
                                                                                  DateTimeOffset now,
                                                                                  bool onlyIfNotBackedOff,
                                                                                  CancellationToken cancellationToken)
    {
        OutsideTransaction.Require(_dbContext);

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var current = await Rows(userId, action).AsNoTracking()
                                                    .Select(a => new { a.FailedCount, a.LockoutEnd })
                                                    .SingleOrDefaultAsync(cancellationToken);

            if (onlyIfNotBackedOff && current?.LockoutEnd > now)
            {
                return null;
            }

            int failedCount = (current?.FailedCount ?? 0) + 1;
            DateTimeOffset? lockoutEnd = failedCount > _options.MaxFailedAttempts ?
                now.AddSeconds(BackoffSeconds(failedCount)) :
                current?.LockoutEnd;

            // Written only over the values just read, so a parallel count in between makes this one
            // miss and go round again rather than overwrite it.
            bool counted = current is null ?
                await TryInsertAsync(userId, action, failedCount, lockoutEnd, cancellationToken) :
                await Rows(userId, action).Where(a => a.FailedCount == current.FailedCount && a.LockoutEnd == current.LockoutEnd)
                                          .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.FailedCount, failedCount)
                                                                                .SetProperty(a => a.LockoutEnd, lockoutEnd),
                                                              cancellationToken) > 0;

            if (!counted)
            {
                continue;
            }

            if (failedCount > _options.MaxFailedAttempts)
            {
                _logger.LogWarning("User {UserId} backed off from {Action} for {LockoutSeconds}s after " +
                                   "{FailedAttempts} failed attempts.",
                                   userId, action, BackoffSeconds(failedCount), failedCount);
            }

            return (failedCount, lockoutEnd);
        }

        return null;
    }

    /// <summary>
    /// The backoff a failure count past the threshold earns: doubling from the base on each failure
    /// past it, capped. Shifting is done on a long and clamped before it reaches the timestamp, so a
    /// misconfigured base cannot overflow into a negative - which would read as "not locked out" and
    /// silently disable the cap.
    /// </summary>
    private long BackoffSeconds(int failedCount)
    {
        int doublings = Math.Min(failedCount - _options.MaxFailedAttempts - 1, 30);

        return Math.Min((long)_options.LockoutBaseSeconds << doublings, _options.LockoutMaxSeconds);
    }

    /// <summary>
    /// Inserts the account's first row for <paramref name="action"/>, or answers false when a parallel
    /// request inserted it first - which the caller then reads and updates like any other.
    /// </summary>
    /// <remarks>
    /// The unique index on the user and the action is what refuses the second insert. Rather than read
    /// a provider's error code, the refusal is recognised by the row now being there; any other failure
    /// is rethrown.
    /// </remarks>
    private async Task<bool> TryInsertAsync(long userId,
                                            LimitedAction action,
                                            int failedCount,
                                            DateTimeOffset? lockoutEnd,
                                            CancellationToken cancellationToken)
    {
        UserActionAttempt attempt = new() { UserId = userId, Action = action, FailedCount = failedCount, LockoutEnd = lockoutEnd };
        _dbContext.UserActionAttempts.Add(attempt);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(attempt).State = EntityState.Detached;

            if (await Rows(userId, action).AnyAsync(cancellationToken))
            {
                return false;
            }

            throw;
        }
        finally
        {
            // Never left tracked: every later read and write here goes straight to the row.
            _dbContext.Entry(attempt).State = EntityState.Detached;
        }
    }
}
