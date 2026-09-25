using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The limiter under parallel requests: every attempt counted, a burst compared no more often than a
/// patient caller would be, and one cooldown started however many ask at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>One context per worker, as one per request</b>, over one database file in WAL, every connection
/// opened before a gate releases them together. A shared context would serialise the workers and show
/// nothing: the defects these pin were lost updates between separately loaded copies of a row.
/// </para>
/// <para>
/// Before the fix, eight parallel failures against an existing row left a count of one or two, eight
/// first-ever failures raised seven unique-index violations, and eight workers at the allowance all
/// passed the check.
/// </para>
/// </remarks>
public sealed class AttemptLimiterConcurrencyTests : IDisposable
{
    private const int Workers = 12;
    private const int MaxAttempts = 5;

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-limitrace-{Guid.NewGuid():N}.db");

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

    private HomespoolDbContext NewContext()
    {
        return new HomespoolDbContext(new DbContextOptionsBuilder<HomespoolDbContext>()
                                      .UseSqlite($"Data Source={_databasePath}")
                                      .Options);
    }

    private static AttemptLimiter NewLimiter(HomespoolDbContext context)
    {
        return new AttemptLimiter(context,
                                  TestOptions.Snapshot(new AttemptLimitOptions
                                  {
                                      MaxFailedAttempts = MaxAttempts,
                                      LockoutBaseSeconds = 30,
                                      LockoutMaxSeconds = 3600,
                                  }),
                                  NullLogger<AttemptLimiter>.Instance);
    }

    private async Task<HSUser> SeedAsync()
    {
        await using HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", TestContext.Current.CancellationToken);

        HSUser user = new("racer@example.com") { Email = "racer@example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    /// <summary>
    /// Runs <paramref name="work"/> once per worker, each on a context of its own with its connection
    /// already open, all released together.
    /// </summary>
    private async Task<T[]> InParallelAsync<T>(Func<AttemptLimiter, Task<T>> work)
    {
        List<HomespoolDbContext> contexts = [];

        try
        {
            for (int i = 0; i < Workers; i++)
            {
                HomespoolDbContext context = NewContext();
                contexts.Add(context);
                await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                await context.UserActionAttempts.AnyAsync(TestContext.Current.CancellationToken);
            }

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<T>[] running = contexts.Select(context => Task.Run(async () =>
                                                                    {
                                                                        await gate.Task;

                                                                        return await work(NewLimiter(context));
                                                                    },
                                                                    TestContext.Current.CancellationToken))
                                        .ToArray();
            gate.SetResult();

            return await Task.WhenAll(running);
        }
        finally
        {
            foreach (HomespoolDbContext context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private async Task<UserActionAttempt?> StoredAsync(HSUser user, LimitedAction action)
    {
        await using HomespoolDbContext context = NewContext();

        return await context.UserActionAttempts.AsNoTracking()
                            .SingleOrDefaultAsync(a => a.UserId == user.Id && a.Action == action, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ParallelFailuresAreEveryOneCounted()
    {
        HSUser user = await SeedAsync();

        // No row yet, so every worker races to insert the first one.
        await InParallelAsync(async limiter =>
        {
            await limiter.RecordFailedAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);

            return true;
        });

        (await StoredAsync(user, LimitedAction.ClaimPrinter))!.FailedCount.Should().Be(Workers, "no failure may overwrite another");
    }

    [Fact]
    public async Task AParallelBurstIsComparedOnlyAsOftenAsAPatientCallerWouldBe()
    {
        HSUser user = await SeedAsync();

        AttemptTicket[] tickets = await InParallelAsync(limiter => limiter.TakeAttemptAsync(user.Id, LimitedAction.StepUp, Now, CancellationToken.None));

        tickets.Count(ticket => ticket.Taken).Should().Be(MaxAttempts + 1,
                                                          "the allowance, and then the one attempt that crosses it and imposes the backoff");
        tickets.Count(ticket => ticket.LockoutImposed is not null).Should().Be(1, "exactly one attempt crossed the threshold");
        tickets.Where(ticket => !ticket.Taken).Should().AllSatisfy(ticket => ticket.BackedOff.Should().Be(TimeSpan.FromSeconds(30)));

        UserActionAttempt stored = (await StoredAsync(user, LimitedAction.StepUp))!;
        stored.FailedCount.Should().Be(MaxAttempts + 1, "a refused attempt is not counted");
        stored.LockoutEnd.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public async Task ParallelUsesStartOneCooldownAndGoAheadOnce()
    {
        HSUser user = await SeedAsync();

        TimeSpan?[] refusals = await InParallelAsync(limiter => limiter.TryStartCooldownAsync(user.Id, LimitedAction.ChangeSignIn, Now, TimeSpan.FromMinutes(5), CancellationToken.None));

        refusals.Count(refusal => refusal is null).Should().Be(1, "one use starts the cooldown and every other is refused by it");
        refusals.Where(refusal => refusal is not null).Should().AllSatisfy(refusal => refusal.Should().Be(TimeSpan.FromMinutes(5)));
        (await StoredAsync(user, LimitedAction.ChangeSignIn))!.LockoutEnd.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public async Task ParallelUsesAfterALapsedCooldownStartOneAgain()
    {
        HSUser user = await SeedAsync();

        await using (HomespoolDbContext context = NewContext())
        {
            (await NewLimiter(context).TryStartCooldownAsync(user.Id, LimitedAction.ChangeSignIn, Now.AddHours(-1), TimeSpan.FromMinutes(5), CancellationToken.None))
                .Should().BeNull();
        }

        // The row exists now, so every worker races to update it rather than to insert it.
        TimeSpan?[] refusals = await InParallelAsync(limiter => limiter.TryStartCooldownAsync(user.Id, LimitedAction.ChangeSignIn, Now, TimeSpan.FromMinutes(5), CancellationToken.None));

        refusals.Count(refusal => refusal is null).Should().Be(1, "the lapsed cooldown is replaced once, and that one is what refuses the rest");
        (await StoredAsync(user, LimitedAction.ChangeSignIn))!.LockoutEnd.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public async Task ARefusedUseDoesNotRestartTheCooldown()
    {
        HSUser user = await SeedAsync();
        await using HomespoolDbContext context = NewContext();
        AttemptLimiter limiter = NewLimiter(context);

        (await limiter.TryStartCooldownAsync(user.Id, LimitedAction.ChangeEmail, Now, TimeSpan.FromMinutes(1), CancellationToken.None)).Should().BeNull();
        (await limiter.TryStartCooldownAsync(user.Id, LimitedAction.ChangeEmail, Now.AddSeconds(40), TimeSpan.FromMinutes(1), CancellationToken.None))
            .Should().Be(TimeSpan.FromSeconds(20));
        (await limiter.TryStartCooldownAsync(user.Id, LimitedAction.ChangeEmail, Now.AddMinutes(1), TimeSpan.FromMinutes(1), CancellationToken.None))
            .Should().BeNull("the first cooldown has run out, and the refused use in the middle did not extend it");
    }

    [Fact]
    public async Task ARightAnswerOnTheAttemptThatCrossedTheThresholdLiftsItsBackoff()
    {
        HSUser user = await SeedAsync();
        await using HomespoolDbContext context = NewContext();
        AttemptLimiter limiter = NewLimiter(context);

        for (int i = 0; i < MaxAttempts; i++)
        {
            (await limiter.TakeAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None)).LockoutImposed.Should().BeNull();
        }

        AttemptTicket crossing = await limiter.TakeAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        crossing.LockoutImposed.Should().Be(Now.AddSeconds(30));

        // A right code refused for another reason: not a guess, so the attempt goes back.
        await limiter.ReturnAttemptAsync(user.Id, LimitedAction.ClaimPrinter, crossing, CancellationToken.None);

        UserActionAttempt stored = (await StoredAsync(user, LimitedAction.ClaimPrinter))!;
        stored.FailedCount.Should().Be(MaxAttempts);
        stored.LockoutEnd.Should().BeNull("the backoff was this attempt's own");
    }

    [Fact]
    public async Task AReturnedFirstAttemptLeavesNoRow()
    {
        HSUser user = await SeedAsync();
        await using HomespoolDbContext context = NewContext();
        AttemptLimiter limiter = NewLimiter(context);

        AttemptTicket attempt = await limiter.TakeAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);
        await limiter.ReturnAttemptAsync(user.Id, LimitedAction.ClaimPrinter, attempt, CancellationToken.None);

        (await StoredAsync(user, LimitedAction.ClaimPrinter)).Should().BeNull();
    }

    [Fact]
    public async Task AResetInsideARolledBackTransactionIsUndone()
    {
        HSUser user = await SeedAsync();
        await using HomespoolDbContext context = NewContext();
        AttemptLimiter limiter = NewLimiter(context);

        await limiter.TakeAttemptAsync(user.Id, LimitedAction.ClaimPrinter, Now, CancellationToken.None);

        await using (IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await limiter.ResetAsync(user.Id, LimitedAction.ClaimPrinter, CancellationToken.None);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await StoredAsync(user, LimitedAction.ClaimPrinter))!.FailedCount.Should().Be(1, "the claim did not land, so neither did its reset");
    }

    [Fact]
    public async Task AnAttemptIsNeverCountedInsideATransaction()
    {
        HSUser user = await SeedAsync();
        await using HomespoolDbContext context = NewContext();
        AttemptLimiter limiter = NewLimiter(context);

        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Func<Task> take = () => limiter.TakeAttemptAsync(user.Id, LimitedAction.StepUp, Now, CancellationToken.None);

        await take.Should().ThrowAsync<InvalidOperationException>("a rollback would uncount it");
    }
}
