using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Bounds how fast one account can guess registration codes on the claim page, with an exponential
/// backoff that always self-heals.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a ten-character code safe on the <i>authenticated</i> path. The anonymous
/// <c>/p/register</c> endpoints already sit behind a global 300/min limiter, but the claim page has
/// no limiter of its own, so without this an account could grind the code space at request rate.
/// </para>
/// <para>
/// <b>Backoff, never invalidation.</b> Burning a pending registration after N wrong guesses would
/// make the cap an enrolment denial-of-service. Backing the <i>caller</i> off instead leaves the
/// printer's code untouched, and the person is standing at the printer anyway, where a fresh code
/// costs one menu press.
/// </para>
/// <para>
/// Writes through <see cref="HSDbContext"/> rather than <c>UserManager.UpdateAsync</c> deliberately:
/// that would run the user validators and save on its own schedule, and a counter bump has no
/// business failing because some unrelated field stopped validating.
/// </para>
/// </remarks>
public class ClaimAttemptLimiter
{
    private readonly HSDbContext _dbContext;
    private readonly PrusaConnectOptions _options;
    private readonly ILogger<ClaimAttemptLimiter> _logger;

    public ClaimAttemptLimiter(HSDbContext dbContext,
                               IOptions<PrusaConnectOptions> options,
                               ILogger<ClaimAttemptLimiter> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// How much longer this account is backed off for, or null if it may attempt a claim now.
    /// </summary>
    /// <param name="user">The signed-in user attempting the claim.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    public TimeSpan? RemainingLockout(HSUser user, DateTimeOffset now)
    {
        if (user.ClaimLockoutEnd is not { } lockoutEnd || lockoutEnd <= now)
        {
            return null;
        }

        return lockoutEnd - now;
    }

    /// <summary>
    /// Records a submitted code that matched no pending registration, backing the account off once
    /// <see cref="PrusaConnectOptions.MaxFailedClaimAttempts"/> is passed.
    /// </summary>
    /// <remarks>
    /// Saves on its own, because it runs after the claim transaction has rolled back - a failure
    /// recorded inside that transaction would be rolled back with it, which is precisely the
    /// "rollback undoes the thing you were counting on" trap.
    /// </remarks>
    /// <param name="user">The signed-in user whose attempt failed.</param>
    /// <param name="now">The current time, taken once by the caller.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task RecordFailedAttemptAsync(HSUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        user.FailedClaimAttempts++;

        int over = user.FailedClaimAttempts - _options.MaxFailedClaimAttempts;

        if (over > 0)
        {
            // Doubling from the base on each failure past the threshold, capped. Shifting is done on
            // a long and clamped before it reaches TimeSpan, so a misconfigured base cannot overflow
            // into a negative - which would read as "not locked out" and silently disable the cap.
            int doublings = Math.Min(over - 1, 30);
            long seconds = Math.Min((long)_options.ClaimLockoutBaseSeconds << doublings,
                                    _options.ClaimLockoutMaxSeconds);

            user.ClaimLockoutEnd = now.AddSeconds(seconds);

            _logger.LogWarning("User {UserId} backed off from claiming for {LockoutSeconds}s after "
                               + "{FailedAttempts} unrecognised registration codes.",
                               user.Id, seconds, user.FailedClaimAttempts);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Clears the failure count after a successful claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saves through the same request-scoped <see cref="HSDbContext"/> the caller's transaction is
    /// ambient over (see <c>UnitOfWork</c>), so calling this inside a claim transaction enrolls the
    /// reset in it and a rollback takes the reset with it - which is what makes "the counter is
    /// cleared only if the claim actually landed" true rather than merely intended.
    /// </para>
    /// <para>
    /// A no-op when there is nothing to clear, which is the overwhelmingly common case: it keeps an
    /// ordinary first-try claim from writing a row nobody changed.
    /// </para>
    /// </remarks>
    /// <param name="user">The signed-in user whose claim succeeded.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task ResetAsync(HSUser user, CancellationToken cancellationToken)
    {
        if (user.FailedClaimAttempts == 0 && user.ClaimLockoutEnd is null)
        {
            return;
        }

        user.FailedClaimAttempts = 0;
        user.ClaimLockoutEnd = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
