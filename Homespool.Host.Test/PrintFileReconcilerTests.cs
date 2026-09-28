using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

using Homespool.Data;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintFileReconciler"/> - teaching the index what happened while the process was not
/// running.
/// </summary>
/// <remarks>
/// Every case here is a divergence that cannot arise through the app: a file copied in by hand, one
/// deleted from underneath it, an account removed. The property being pinned throughout is the
/// direction of authority - <b>the disk teaches the table and never the reverse</b>.
/// </remarks>
public sealed class PrintFileReconcilerTests : IDisposable
{
    private const long Alice = 1;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-reconcile-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-reconcile-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
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

    /// <summary>
    /// A file that arrived without going through the app is indexed - and deliberately not hashed,
    /// which is what keeps startup from reading the whole store.
    /// </summary>
    [Fact]
    public async Task AFileOnDiskWithNoRowIsIndexedWithoutADigest()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "1-alice", "handcopied.gcode"), [1, 2, 3],
                                      TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Name.Should().Be("handcopied.gcode");
        row.Size.Should().Be(3);
        row.Digest.Should().BeNull();
        row.MetadataState.Should().Be(PrintFileMetadataState.Unread, "indexed, but nobody has read it");
    }

    /// <summary>A row whose file left without us is removed - the disk is the truth.</summary>
    [Fact]
    public async Task ARowWhoseFileIsGoneIsRemoved()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        context.PrintFiles.Add(new PrintFile
        {
            UserId = Alice,
            Name = "vanished.gcode",
            Size = 3,
            UploadedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// The one case where queue entries are removed without asking: their file left without going
    /// through us, so there is nobody to ask and nothing left to print.
    /// </summary>
    [Fact]
    public async Task QueuedPrintsForAVanishedFileAreCancelled()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        PrintFile row = new()
        {
            UserId = Alice,
            Name = "vanished.gcode",
            Size = 3,
            UploadedAt = DateTimeOffset.UnixEpoch,
        };

        context.PrintFiles.Add(row);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = printer.Id,
            PrintFileId = row.Id,
            Position = 0,
            QueuedByUserId = Alice,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// Bytes replaced underneath us: the size follows, and the digest is cleared rather than left
    /// describing content that is gone.
    /// </summary>
    [Fact]
    public async Task ChangedBytesCorrectTheSizeAndClearTheDigest()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));
        string path = Path.Combine(_root, "1-alice", "edited.gcode");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6], TestContext.Current.CancellationToken);

        context.PrintFiles.Add(new PrintFile
        {
            UserId = Alice,
            Name = "edited.gcode",
            Size = 3,
            Digest = "stale",
            UploadedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Size.Should().Be(6);
        row.Digest.Should().BeNull("a digest for content that is gone would be believed");
    }

    /// <summary>
    /// A file nobody touched is not "corrected": the disk's timestamp is finer than the column's, and
    /// the comparison has to be made at the precision the row can actually hold.
    /// </summary>
    /// <remarks>
    /// The row is written the way an upload writes it - from the store's own description of the file
    /// - and read back through the database, so the only difference between the two timestamps is what
    /// the round trip took off. The mtime is set explicitly, because a filesystem that happened to
    /// store whole milliseconds would make this pass without testing anything.
    /// </remarks>
    [Fact]
    public async Task AnUntouchedFileKeepsItsDigestAcrossARoundTrip()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));
        string path = Path.Combine(_root, "1-alice", "untouched.gcode");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        DateTime written = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        File.SetLastWriteTimeUtc(path, written);
        File.GetLastWriteTimeUtc(path).Ticks.Should().Be(written.Ticks, "the filesystem must keep sub-millisecond ticks for this to test anything");

        StoredFile stored = NewStore().Find(Alice, "untouched.gcode")!;

        context.PrintFiles.Add(new PrintFile
        {
            UserId = Alice,
            Name = stored.FileName,
            Size = stored.Length,
            Digest = "uploaded",
            UploadedAt = stored.UploadedAt,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be("uploaded", "nothing about the file changed");
        logger.Collector.GetSnapshot().Should().NotContain(record => record.Level == LogLevel.Information,
                                                           "an index that already matched reports nothing corrected");
    }

    /// <summary>
    /// An edit that keeps the length - a temperature changed from 215 to 220 - is still a change: the
    /// timestamp is what catches it, so the comparison must not have become size-only.
    /// </summary>
    [Fact]
    public async Task AnEditThatKeepsTheSizeStillClearsTheDigest()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        string path = await WriteFileAsync("edited.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "edited.gcode", "uploaded");

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().BeNull("the bytes may have changed, and the old digest would be believed");
    }

    /// <summary>
    /// A row with no digest gets exactly the one an upload of the same bytes would have written -
    /// the reprint check compares the two, so a different encoding would read as a changed file.
    /// </summary>
    [Fact]
    public async Task TheBackfillWritesTheDigestAnUploadWould()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await using MemoryStream content = new([1, 2, 3, 4, 5]);
        PublishedFile uploaded = await NewStore().SaveAsync(Alice, "model.gcode", content, overwrite: false,
                                                            TestContext.Current.CancellationToken, "alice");

        await AddRowFromDiskAsync(context, "model.gcode", digest: null);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.BackfillDigestsAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be(uploaded.Digest);
        logger.Collector.GetSnapshot()
              .Should().ContainSingle(record => record.Level == LogLevel.Information)
              .Which.StructuredState.Should().Contain(property => property.Key == "Count" && property.Value == "1");
    }

    /// <summary>A digest that is already there is not the backfill's to replace.</summary>
    [Fact]
    public async Task TheBackfillLeavesAnExistingDigestAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await WriteFileAsync("kept.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "kept.gcode", "uploaded");

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.BackfillDigestsAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be("uploaded");
    }

    /// <summary>
    /// A digest is written only for the bytes the row describes. Size and timestamp each have to
    /// match on their own, so each gets a file that differs in that alone.
    /// </summary>
    [Fact]
    public async Task TheBackfillWritesNothingForAFileThatNoLongerMatchesItsRow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await WriteFileAsync("longer.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "longer.gcode", digest: null, sizeOffset: 1);

        string touched = await WriteFileAsync("touched.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "touched.gcode", digest: null);
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(1));

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.BackfillDigestsAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        (await context.PrintFiles.Select(row => row.Digest).ToListAsync(TestContext.Current.CancellationToken))
            .Should().AllSatisfy(digest => digest.Should().BeNull("a digest of other bytes would be believed"));
    }

    /// <summary>
    /// One file that cannot be read costs its own digest and nothing else.
    /// </summary>
    [Fact]
    public async Task TheBackfillSkipsAnUnreadableFileAndFillsTheRest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        string locked = await WriteFileAsync("locked.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "locked.gcode", digest: null);
        await WriteFileAsync("open.gcode", [4, 5, 6]);
        await AddRowFromDiskAsync(context, "open.gcode", digest: null);

        File.SetUnixFileMode(locked, UnixFileMode.None);
        Assert.SkipWhen(CanRead(locked), "this user reads files regardless of their mode");

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.BackfillDigestsAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        (await context.PrintFiles.SingleAsync(row => row.Name == "locked.gcode", TestContext.Current.CancellationToken))
            .Digest.Should().BeNull();
        (await context.PrintFiles.SingleAsync(row => row.Name == "open.gcode", TestContext.Current.CancellationToken))
            .Digest.Should().NotBeNull();
        logger.Collector.GetSnapshot().Should().ContainSingle(record => record.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Started as the service it is: the reconcile first, then the digests - so a file that arrived
    /// by hand is both indexed and hashed by one start. In the other order its row would not exist
    /// yet when the backfill looked.
    /// </summary>
    [Fact]
    public async Task StartingTheServiceIndexesAndThenHashes()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await WriteFileAsync("handcopied.gcode", [1, 2, 3]);

        using PrintFileReconciler reconciler = NewReconciler();

        // Act
        await reconciler.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        await WaitForDigestAsync(context, digest => digest is not null);
        await reconciler.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A file edited in place while the service runs: its size and timestamp are corrected and its
    /// digest cleared, so the reprint check stops believing a digest of bytes that are gone.
    /// </summary>
    [Fact]
    public async Task TheRecheckCorrectsAFileEditedWhileRunning()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        string path = await WriteFileAsync("edited.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "edited.gcode", "uploaded");

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.RecheckAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Size.Should().Be(4);
        row.Digest.Should().BeNull("a digest for content that is gone would be believed");
    }

    /// <summary>
    /// The running pass adds and removes nothing: each would race a rename or an upload that has
    /// changed the disk and not yet the table. That is the startup pass's work.
    /// </summary>
    [Fact]
    public async Task TheRecheckNeitherAddsNorRemovesRows()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await WriteFileAsync("unindexed.gcode", [1, 2, 3]);

        context.PrintFiles.Add(new PrintFile
        {
            UserId = Alice,
            Name = "renaming.gcode",
            Size = 3,
            Digest = "uploaded",
            UploadedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.RecheckAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        row.Name.Should().Be("renaming.gcode", "a row whose file is missing may be mid-rename");
        row.Digest.Should().Be("uploaded");
    }

    /// <summary>
    /// Every <see cref="PrintFileReconciler.RecheckInterval"/> the running service rechecks and then
    /// re-hashes: a file edited in place ends up with the digest of its new bytes, not the old one
    /// and not none.
    /// </summary>
    [Fact]
    public async Task TheServiceRechecksAndRehashesOnEachInterval()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        string path = await WriteFileAsync("edited.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "edited.gcode", digest: null);

        FakeTimeProvider time = new();
        using PrintFileReconciler reconciler = NewReconciler(timeProvider: time);

        await reconciler.StartAsync(TestContext.Current.CancellationToken);
        string? before = await WaitForDigestAsync(context, digest => digest is not null);

        await File.WriteAllBytesAsync(path, [4, 5, 6, 7], TestContext.Current.CancellationToken);

        // Act
        time.Advance(PrintFileReconciler.RecheckInterval);

        // Assert
        await WaitForDigestAsync(context, digest => digest is not null && digest != before);
        await reconciler.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A removed account's files are left exactly where they are - indexing them would break the
    /// foreign key, and deleting them would be this class writing to the disk, which it never does.
    /// </summary>
    [Fact]
    public async Task ADirectoryBelongingToNoUserIsLeftAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        Directory.CreateDirectory(Path.Combine(_root, "999-ghost"));
        string path = Path.Combine(_root, "999-ghost", "orphan.gcode");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        File.Exists(path).Should().BeTrue("the reconciler never writes to the disk");
    }

    private static async Task AddUserAsync(HomespoolDbContext context)
    {
        const string email = "alice@example.com";

        context.Users.Add(new HSUser(email)
        {
            Id = Alice,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static bool CanRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string> WriteFileAsync(string name, byte[] content)
    {
        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));
        string path = Path.Combine(_root, "1-alice", name);
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);

        return path;
    }

    /// <summary>
    /// Writes the row the way an upload does, from the store's own description of the file on disk.
    /// </summary>
    private async Task AddRowFromDiskAsync(HomespoolDbContext context, string name, string? digest, long sizeOffset = 0)
    {
        StoredFile stored = NewStore().Find(Alice, name)!;

        context.PrintFiles.Add(new PrintFile
        {
            UserId = Alice,
            Name = stored.FileName,
            Size = stored.Length + sizeOffset,
            Digest = digest,
            UploadedAt = stored.UploadedAt,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Polls the single row until its digest satisfies <paramref name="condition"/>, for a service
    /// whose loop runs on its own schedule. Returns the digest it saw.
    /// </summary>
    private static async Task<string?> WaitForDigestAsync(HomespoolDbContext context, Func<string?, bool> condition)
    {
        string? digest = null;

        for (int i = 0; i < 500; i++)
        {
            digest = await context.PrintFiles
                                  .AsNoTracking()
                                  .Select(row => row.Digest)
                                  .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            if (condition(digest))
            {
                return digest;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition(digest).Should().BeTrue("the service's pass should have run by now");

        return digest;
    }

    private PrintFileReconciler NewReconciler(ILogger<PrintFileReconciler>? logger = null, TimeProvider? timeProvider = null)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

        return new PrintFileReconciler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                                       NewStore(),
                                       TestOptions.Monitor(new PrintFileStorageOptions { Directory = _root }),
                                       new HostEnvironmentAccessor(_root),
                                       timeProvider ?? TimeProvider.System,
                                       logger ?? NullLogger<PrintFileReconciler>.Instance);
    }

    private UserFileStore NewStore()
    {
        return new UserFileStore(TestOptions.Monitor(new PrintFileStorageOptions { Directory = _root }),
                                 new HostEnvironmentAccessor(_root),
                                 TimeProvider.System,
                                 NullLogger<UserFileStore>.Instance);
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
