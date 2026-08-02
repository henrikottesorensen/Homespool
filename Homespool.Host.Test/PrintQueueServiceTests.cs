using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintQueueService"/> - the list a printer pulls from, and who may change it.
/// </summary>
/// <remarks>
/// The permission split is the part worth pinning: <b>one shared queue per printer</b>, changed by
/// anyone with <c>CanUse</c> including entries somebody else added, and merely readable with
/// <c>CanRead</c> (<c>notes/print-queue.md</c>).
/// </remarks>
public sealed class PrintQueueServiceTests : IDisposable
{
    private const long Alice = 1;
    private const long Bob = 2;

    private readonly QueueSignal _signal = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-queue-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-queue-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        _signal.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task QueuedFilesComeBackInTheOrderTheyWereAdded()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode", "two.gcode", "three.gcode");

        // Act
        await queue.EnqueueAsync(printer.Id, Alice, "one.gcode", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "two.gcode", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "three.gcode", TestContext.Current.CancellationToken);

        // Assert
        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);

        jobs.Select(job => job.PrintFile!.Name).Should().Equal("one.gcode", "two.gcode", "three.gcode");
    }

    /// <summary>
    /// Moving renumbers the queue rather than swapping two rows, so the positions keep describing the
    /// order plainly.
    /// </summary>
    [Fact]
    public async Task MovingAJobReordersTheQueueAndRenumbersIt()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode", "two.gcode", "three.gcode");

        await queue.EnqueueAsync(printer.Id, Alice, "one.gcode", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "two.gcode", TestContext.Current.CancellationToken);
        QueuedPrint third = await queue.EnqueueAsync(printer.Id, Alice, "three.gcode",
            TestContext.Current.CancellationToken);

        // Act
        bool moved = await queue.MoveAsync(third.Id, Alice, 0, TestContext.Current.CancellationToken);

        // Assert
        moved.Should().BeTrue();

        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);

        jobs.Select(job => job.PrintFile!.Name).Should().Equal("three.gcode", "one.gcode", "two.gcode");
        jobs.Select(job => job.Position).Should().Equal(0, 1, 2);
    }

    /// <summary>An index past the end is clamped rather than refused - "send it to the back".</summary>
    [Fact]
    public async Task MovingPastTheEndPutsAJobLast()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode", "two.gcode");

        QueuedPrint first = await queue.EnqueueAsync(printer.Id, Alice, "one.gcode",
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "two.gcode", TestContext.Current.CancellationToken);

        // Act
        await queue.MoveAsync(first.Id, Alice, 99, TestContext.Current.CancellationToken);

        // Assert
        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);

        jobs.Select(job => job.PrintFile!.Name).Should().Equal("two.gcode", "one.gcode");
    }

    /// <summary>
    /// Cancelling from the middle leaves a gap in the positions, which is harmless - the order is the
    /// sort, not the values - and a later enqueue must not collide with a position still in use.
    /// </summary>
    [Fact]
    public async Task EnqueueingAfterACancelDoesNotReuseAPosition()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode", "two.gcode", "three.gcode");

        await queue.EnqueueAsync(printer.Id, Alice, "one.gcode", TestContext.Current.CancellationToken);
        QueuedPrint second = await queue.EnqueueAsync(printer.Id, Alice, "two.gcode",
            TestContext.Current.CancellationToken);

        // Act
        await queue.CancelAsync(second.Id, Alice, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "three.gcode", TestContext.Current.CancellationToken);

        // Assert
        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);

        jobs.Select(job => job.PrintFile!.Name).Should().Equal("one.gcode", "three.gcode");
        jobs.Select(job => job.Position).Should().OnlyHaveUniqueItems();
    }

    /// <summary>The same file twice is allowed - two copies is an ordinary thing to want.</summary>
    [Fact]
    public async Task TheSameFileCanBeQueuedTwice()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode");

        // Act
        await queue.EnqueueAsync(printer.Id, Alice, "one.gcode", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(printer.Id, Alice, "one.gcode", TestContext.Current.CancellationToken);

        // Assert
        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);

        jobs.Should().HaveCount(2);
        jobs.Select(job => job.PrintFileId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task QueueingAFileTheCallerDoesNotHaveIsRefused()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);

        // Act
        Func<Task> act = () => queue.EnqueueAsync(printer.Id, Alice, "nothing.gcode",
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<PrintFileNotFoundException>();
    }

    /// <summary>
    /// <c>CanRead</c> sees the queue but cannot change it - the split this service exists to hold.
    /// </summary>
    [Fact]
    public async Task ReadingIsAllowedWithoutCanUseButChangingIsNot()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: false);
        PrintQueueService queue = NewQueue(context);

        // Act
        IReadOnlyList<QueuedPrint> jobs = await queue.ListAsync(printer.Id, Alice, TestContext.Current.CancellationToken);
        Func<Task> enqueue = () => queue.EnqueueAsync(printer.Id, Alice, "one.gcode",
            TestContext.Current.CancellationToken);

        // Assert
        jobs.Should().BeEmpty();
        await enqueue.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>A person with no membership at all sees nothing, rather than an empty queue.</summary>
    [Fact]
    public async Task SomeoneOutsideTheTeamCannotEvenReadTheQueue()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);

        // Act
        Func<Task> act = () => queue.ListAsync(printer.Id, Bob, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// The queue is the printer's, not the queuer's: anyone with <c>CanUse</c> may cancel anyone's
    /// entry.
    /// </summary>
    [Fact]
    public async Task AnotherMemberWithCanUseMayCancelSomebodyElsesJob()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await SeedAsync(context, canUse: true);
        await AddMemberAsync(context, printer.TeamId, Bob, canUse: true);
        PrintQueueService queue = NewQueue(context);
        await UploadAsync(context, "one.gcode");

        QueuedPrint job = await queue.EnqueueAsync(printer.Id, Alice, "one.gcode",
            TestContext.Current.CancellationToken);

        // Act
        bool cancelled = await queue.CancelAsync(job.Id, Bob, TestContext.Current.CancellationToken);

        // Assert
        cancelled.Should().BeTrue();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task CancellingAJobThatIsNotThereIsFalseRatherThanAThrow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await SeedAsync(context, canUse: true);
        PrintQueueService queue = NewQueue(context);

        // Act
        bool cancelled = await queue.CancelAsync(4242, Alice, TestContext.Current.CancellationToken);

        // Assert
        cancelled.Should().BeFalse();
    }

    private static async Task AddMemberAsync(HSDbContext context, int teamId, long userId, bool canUse)
    {
        context.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            CanRead = true,
            CanUse = canUse,
            CanManage = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A user, a team they belong to, and a printer that team owns.</summary>
    private async Task<Printer> SeedAsync(HSDbContext context, bool canUse)
    {
        HSUser user = new("alice@example.com")
        {
            Id = Alice,
            Email = "alice@example.com",
            NormalizedEmail = "ALICE@EXAMPLE.COM",
            NormalizedUserName = "ALICE@EXAMPLE.COM",
        };

        context.Users.Add(user);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddMemberAsync(context, team.Id, Alice, canUse);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    /// <summary>Puts real files on disk for Alice, so the catalog has something to resolve.</summary>
    private async Task UploadAsync(HSDbContext context, params string[] names)
    {
        PrintFileCatalog catalog = NewCatalog(context);

        foreach (string name in names)
        {
            await catalog.SaveAsync(Alice, name, new MemoryStream([1, 2, 3]), overwrite: false,
                TestContext.Current.CancellationToken);
        }
    }

    private PrintFileCatalog NewCatalog(HSDbContext context)
    {
        UserFileStore store = new(Options.Create(new PrintFileStorageOptions { Directory = _root }),
            new HostEnvironmentAccessor(_root),
            TimeProvider.System,
            NullLogger<UserFileStore>.Instance);

        return new PrintFileCatalog(store, context, NullLogger<PrintFileCatalog>.Instance);
    }

    private PrintQueueService NewQueue(HSDbContext context)
    {
        return new(context, new TeamService(context), NewCatalog(context), TimeProvider.System, _signal);
    }

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
