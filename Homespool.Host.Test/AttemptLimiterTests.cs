using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The cap that makes a ten-character claim code safe on the authenticated path, where nothing else
/// bounds guessing - the global rate limiter covers only the anonymous <c>/p/register</c> endpoints.
/// </summary>
/// <remarks>
/// Real SQLite rather than the in-memory provider, matching the other enrolment suites: the counter
/// has to survive a save, and "persisted rather than in memory" is the whole point of the row.
/// </remarks>
public sealed class AttemptLimiterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-claimcap-{Guid.NewGuid():N}.db");

    /// <summary>
    /// What the limiter has recorded against this account for the claim action, or 0 when there is
    /// no row - which is what a reset leaves behind, since it deletes rather than zeroes.
    /// </summary>
    private static async Task<int> FailedCountAsync(HomespoolDbContext context, HSUser user)
    {
        UserActionAttempt? attempt = await context.UserActionAttempts
                                                  .AsNoTracking()
                                                  .SingleOrDefaultAsync(
                                                      a => a.UserId == user.Id
                                                           && a.Action == LimitedAction.ClaimPrinter,
                                                      TestContext.Current.CancellationToken);

        return attempt?.FailedCount ?? 0;
    }

    private static AttemptLimiter NewLimiter(HomespoolDbContext context,
                                                  int maxAttempts = 5,
                                                  int baseSeconds = 30,
                                                  int maxSeconds = 3600)
    {
        return new(context,
                   TestOptions.Snapshot(new AttemptLimitOptions
                   {
                       MaxFailedAttempts = maxAttempts,
                       LockoutBaseSeconds = baseSeconds,
                       LockoutMaxSeconds = maxSeconds,
                   }),
                   NullLogger<AttemptLimiter>.Instance);
    }

    private static async Task<HSUser> SeedUserAsync(HomespoolDbContext context)
    {
        HSUser user = new("claimer@example.com") { Email = "claimer@example.com" };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    /// <summary>
    /// A user who has not failed anything may claim.
    /// </summary>
    [Fact]
    public async Task AnUntouchedAccountIsNotBackedOff()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);

        // Assert
        (await NewLimiter(context).RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None))
            .Should().BeNull();
    }

    /// <summary>
    /// Failures below the threshold count but do not back the account off.
    /// </summary>
    /// <remarks>
    /// The headroom is the point: someone mistyping a code off a low-resolution LCD must not be
    /// locked out on their second attempt. Five matches Identity's own login lockout.
    /// </remarks>
    [Fact]
    public async Task FailuresUpToTheThresholdDoNotLockOut()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);
        AttemptLimiter limiter = NewLimiter(context);

        // Act
        for (int i = 0; i < 5; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        }

        // Assert
        (await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None))
            .Should().BeNull("five is the allowance, not the trigger");
        (await FailedCountAsync(context, user)).Should().Be(5);
    }

    /// <summary>
    /// The failure past the threshold applies the base backoff, and the next one doubles it.
    /// </summary>
    [Fact]
    public async Task PastTheThresholdTheBackoffAppliesAndThenDoubles()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);
        AttemptLimiter limiter = NewLimiter(context);

        for (int i = 0; i < 5; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        }

        // Act
        await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        TimeSpan? first = await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);

        await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        TimeSpan? second = await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);

        // Assert
        first.Should().Be(TimeSpan.FromSeconds(30));
        second.Should().Be(TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The backoff stops doubling at the configured ceiling.
    /// </summary>
    /// <remarks>
    /// Unbounded doubling would eventually exceed any useful horizon, and the person locked out is
    /// overwhelmingly likely to be a legitimate user who mistyped. It must always self-heal.
    /// </remarks>
    [Fact]
    public async Task TheBackoffIsCapped()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);
        AttemptLimiter limiter = NewLimiter(context, maxSeconds: 120);

        // Act
        for (int i = 0; i < 40; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        }

        // Assert
        (await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None))
            .Should()
               .Be(TimeSpan.FromSeconds(120), "the backoff must always self-heal within a bounded time");
    }

    /// <summary>
    /// The backoff expires on its own.
    /// </summary>
    [Fact]
    public async Task TheLockoutLapsesOnceItsTimeHasPassed()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);
        AttemptLimiter limiter = NewLimiter(context);

        for (int i = 0; i < 6; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        }

        // Assert
        (await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now.AddSeconds(31), CancellationToken.None))
            .Should().BeNull();
    }

    /// <summary>
    /// The counter survives being read back from the database, not just held on the tracked entity.
    /// </summary>
    /// <remarks>
    /// This is the assertion the design rests on: state kept only in memory would hand an attacker a
    /// fresh budget on every restart, which is exactly what a persisted counter is for. Asserted
    /// against a second context so a tracked in-memory value cannot satisfy it.
    /// </remarks>
    [Fact]
    public async Task TheCountAndLockoutSurviveARestart()
    {
        // Arrange
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            HSUser user = await SeedUserAsync(context);
            AttemptLimiter limiter = NewLimiter(context);

            for (int i = 0; i < 6; i++)
            {
                await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
            }
        }

        // Act
        await using HomespoolDbContext reopened = NewContext();
        HSUser reloaded = await reopened.Users.SingleAsync(TestContext.Current.CancellationToken);

        // Assert
        (await FailedCountAsync(reopened, reloaded)).Should().Be(6);
        (await NewLimiter(reopened).RemainingLockoutAsync(reloaded.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None))
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A successful claim clears both the count and the backoff.
    /// </summary>
    [Fact]
    public async Task ResetClearsTheCountAndTheLockout()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);
        AttemptLimiter limiter = NewLimiter(context);

        for (int i = 0; i < 6; i++)
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        }

        // Act
        await limiter.ResetAsync(user.Id, LimitedAction.ClaimPrinter, CancellationToken.None);

        // Assert
        // Reset deletes the row rather than zeroing it, so "cleared" is "absent".
        (await FailedCountAsync(context, user)).Should().Be(0);
        (await limiter.RemainingLockoutAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None))
            .Should().BeNull();
    }

    /// <summary>
    /// Resetting an account with nothing to clear writes nothing.
    /// </summary>
    /// <remarks>
    /// Every successful first-try claim calls this, which is the overwhelmingly common path - it
    /// should not put a write on a row nobody changed, inside the claim's own transaction, where
    /// SQLite serialises writers against <c>TelemetryWriter</c>.
    /// </remarks>
    [Fact]
    public async Task ResettingAnUntouchedAccountIsANoOp()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser user = await SeedUserAsync(context);

        // Act
        await NewLimiter(context).ResetAsync(user.Id, LimitedAction.ClaimPrinter, CancellationToken.None);

        // Assert
        context.ChangeTracker.HasChanges().Should().BeFalse("there was nothing to clear");
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
