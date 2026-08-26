using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintHistoryService.CountForUserAsync"/> - the count behind the front page's ordering.
/// </summary>
/// <remarks>
/// <para>
/// <b>The access rule is the part worth pinning.</b> This is a question across printers, so there is
/// no single id to require a capability on; the caller passes the ids it was already granted and the
/// count must stay strictly inside that set. <see cref="CountsNothingForAPrinterNotInTheGrantedSet"/>
/// is the guard - widening it would leak the shape of a rack through a sort order.
/// </para>
/// <para>
/// The window and the "unfinished prints count" rule are pinned beside it, because both are choices
/// a later reader would otherwise be free to assume away.
/// </para>
/// </remarks>
public sealed class PrintHistoryUsageTests : IDisposable
{
    private const long Alice = 1;
    private const long Bob = 2;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-usage-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Jobs are counted per printer, for the person who queued them.</summary>
    [Fact]
    public async Task CountsAPersonsJobsPerPrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int busy, int quiet) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, busy, Alice, count: 3);
        await AddJobsAsync(context, quiet, Alice, count: 1);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [busy, quiet], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage[busy].Jobs.Should().Be(3);
        usage[quiet].Jobs.Should().Be(1);
    }

    /// <summary>
    /// Somebody else's jobs are not yours. The front page says "your printers", and counting the
    /// household's work would make a shared machine outrank the one you personally live on.
    /// </summary>
    [Fact]
    public async Task DoesNotCountSomebodyElsesJobs()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int busy, int quiet) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, busy, Bob, count: 5);
        await AddJobsAsync(context, quiet, Alice, count: 1);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [busy, quiet], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage.ContainsKey(busy).Should().BeFalse();
        usage[quiet].Jobs.Should().Be(1);
    }

    /// <summary>
    /// <b>The access guard.</b> A printer the caller was not granted contributes nothing, even though
    /// its rows are right there in the table and belong to the asking user.
    /// </summary>
    [Fact]
    public async Task CountsNothingForAPrinterNotInTheGrantedSet()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int granted, int withheld) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, granted, Alice, count: 1);
        await AddJobsAsync(context, withheld, Alice, count: 9);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [granted], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage.Should().ContainSingle();
        usage.ContainsKey(withheld).Should().BeFalse();
    }

    /// <summary>Work older than the window does not count, which is what keeps the ordering current.</summary>
    [Fact]
    public async Task IgnoresJobsOlderThanTheWindow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int old, int recent) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, old, Alice, count: 4, agoDays: 200);
        await AddJobsAsync(context, recent, Alice, count: 1, agoDays: 2);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [old, recent], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage.ContainsKey(old).Should().BeFalse();
        usage[recent].Jobs.Should().Be(1);
    }

    /// <summary>
    /// A print running right now counts. It is the strongest evidence there is that you use this
    /// printer, and excluding unfinished jobs would drop it down the page while you watched it work.
    /// </summary>
    [Fact]
    public async Task CountsAPrintThatHasNotFinished()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int printer, _) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, printer, Alice, count: 1, finished: false);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [printer], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage[printer].Jobs.Should().Be(1);
    }

    /// <summary>
    /// The tie-break is carried back with the count, and it is the most recent start rather than the
    /// oldest - two printers on equal counts have to order somehow.
    /// </summary>
    [Fact]
    public async Task ReportsTheMostRecentStartAsTheTieBreak()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (int printer, _) = await SeedTwoPrintersAsync(context);

        await AddJobsAsync(context, printer, Alice, count: 1, agoDays: 30);
        await AddJobsAsync(context, printer, Alice, count: 1, agoDays: 1);

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [printer], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage[printer].Jobs.Should().Be(2);
        usage[printer].LastStartedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(-2));
    }

    /// <summary>An empty grant asks the database nothing and comes back empty.</summary>
    [Fact]
    public async Task ReturnsNothingForAnEmptyGrant()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        IReadOnlyDictionary<int, PrinterUsage> usage = await NewHistory(context).CountForUserAsync(
            Alice, [], Since(days: 90), TestContext.Current.CancellationToken);

        // Assert
        usage.Should().BeEmpty();
    }

    private static DateTimeOffset Since(int days)
    {
        return DateTimeOffset.UtcNow.AddDays(-days);
    }

    private static async Task AddJobsAsync(HomespoolDbContext context,
                                           int printerId,
                                           long userId,
                                           int count,
                                           int agoDays = 1,
                                           bool finished = true)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddDays(-agoDays);

        for (int i = 0; i < count; i++)
        {
            context.PrintJobs.Add(new PrintJob
            {
                TrackingId = Guid.NewGuid(),
                PrinterId = printerId,
                FileName = $"part-{i}.bgcode",
                QueuedByUserId = userId,
                StartedAt = startedAt,
                EndedAt = finished ? startedAt.AddHours(1) : null,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(int first, int second)> SeedTwoPrintersAsync(HomespoolDbContext context)
    {
        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer first = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        Printer second = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.AddRange(first, second);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (first.Id, second.Id);
    }

    private PrintHistoryService NewHistory(HomespoolDbContext context)
    {
        return new PrintHistoryService(context,
                                       new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                       new QueueSnapshotReader(context,
                                                               new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance),
                                                               TimeProvider.System),
                                       new UserNameLookup(context));
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
}
