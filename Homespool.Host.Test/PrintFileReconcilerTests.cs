using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

    private const string FirmwareImageName = "COREONE_firmware_7.0.0.bbf";

    private static readonly byte[] SlicedForMk4S = Encoding.UTF8.GetBytes(
        "G28 ; home\nG1 X10 Y10 F3000\n\n; prusaslicer_config = begin\n" +
        "; printer_model = MK4S\n; nozzle_diameter = 0.4\n; prusaslicer_config = end\n");

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
        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

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
        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
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
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
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
        Directory.CreateDirectory(Path.Combine(_root, "1-alice"));

        HSFile row = new()
        {
            Type = FileType.GCode,
            UserId = Alice,
            Name = "vanished.gcode",
            Size = 3,
            UploadedAt = DateTimeOffset.UnixEpoch,
        };

        context.Files.Add(row);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = printer.Id,
            FileId = row.Id,
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
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// Storage that did not come up is not a store that was emptied: with no directory for the user -
    /// the root missing, or present but empty like an unmounted mount point - the rows and the queue
    /// entries pointing at them are kept, and the gap is reported.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowsAreKeptWhenTheUsersDirectoryIsMissing(bool rootExists)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        if (rootExists)
        {
            Directory.CreateDirectory(_root);
        }

        HSFile row = new()
        {
            Type = FileType.GCode,
            UserId = Alice,
            Name = "unmounted.gcode",
            Size = 3,
            UploadedAt = DateTimeOffset.UnixEpoch,
        };

        context.Files.Add(row);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = printer.Id,
            FileId = row.Id,
            Position = 0,
            QueuedByUserId = Alice,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        logger.Collector.GetSnapshot()
              .Should().ContainSingle(record => record.Level == LogLevel.Warning)
              .Which.StructuredState.Should().Contain(property => property.Key == "UserId" && property.Value == "1");
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

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
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

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

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

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
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

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

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
        await AddRowFromDiskAsync(context, "edited.gcode", "uploaded", printerModel: "MK4S");

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().BeNull("the bytes may have changed, and the old digest would be believed");
        row.MetadataState.Should().Be(PrintFileMetadataState.Unread, "the backfill reads it again");
        row.PrinterModel.Should().BeNull("the compatibility check would hold a queue on the old file's model");
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
        UserFileStore store = NewStore();

        store.Confirm();

        PublishedFile uploaded = await store.SaveAsync(Alice, "model.gcode", content, overwrite: false,
                                                       TestContext.Current.CancellationToken, "alice");

        await AddRowFromDiskAsync(context, "model.gcode", digest: null);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be(uploaded.Digest);
        logger.Collector.GetSnapshot()
              .Should().ContainSingle(record => record.Level == LogLevel.Information)
              .Which.StructuredState.Should().Contain(property => property.Key == "Hashed" && property.Value == "1");
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
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

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

        await WriteFileAsync("longer.gcode", SlicedForMk4S);
        await AddRowFromDiskAsync(context, "longer.gcode", digest: null, PrintFileMetadataState.Unread, sizeOffset: 1);

        string touched = await WriteFileAsync("touched.gcode", SlicedForMk4S);
        await AddRowFromDiskAsync(context, "touched.gcode", digest: null, PrintFileMetadataState.Unread);
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(1));

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        List<HSFile> rows = await context.Files.ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().AllSatisfy(row =>
        {
            row.Digest.Should().BeNull("a digest of other bytes would be believed");
            row.MetadataState.Should().Be(PrintFileMetadataState.Unread, "a description of other bytes would be believed");
        });
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
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        (await context.Files.SingleAsync(row => row.Name == "locked.gcode", TestContext.Current.CancellationToken))
            .Digest.Should().BeNull();
        (await context.Files.SingleAsync(row => row.Name == "open.gcode", TestContext.Current.CancellationToken))
            .Digest.Should().NotBeNull();
        logger.Collector.GetSnapshot().Should().ContainSingle(record => record.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A row nobody has read - or one written before the state existed - gets the columns an upload
    /// would have written, without its digest being touched.
    /// </summary>
    [Theory]
    [InlineData(PrintFileMetadataState.Unread)]
    [InlineData(PrintFileMetadataState.Undefined)]
    public async Task TheBackfillReadsWhatAnUnreadFileWasSlicedFor(PrintFileMetadataState state)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        await WriteFileAsync("sliced.gcode", SlicedForMk4S);
        await AddRowFromDiskAsync(context, "sliced.gcode", "uploaded", state);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.MetadataState.Should().Be(PrintFileMetadataState.Read);
        row.PrinterModel.Should().Be("MK4S");
        row.NozzleDiameter.Should().BeApproximately(0.4f, 0.0001f);
        row.Digest.Should().Be("uploaded");
        logger.Collector.GetSnapshot()
              .Should().ContainSingle(record => record.Level == LogLevel.Information)
              .Which.StructuredState.Should().Contain(property => property.Key == "Described" && property.Value == "1");
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
        await AddRowFromDiskAsync(context, "edited.gcode", "uploaded", printerModel: "MK4S");

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.RecheckAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Size.Should().Be(4);
        row.Digest.Should().BeNull("a digest for content that is gone would be believed");
        row.MetadataState.Should().Be(PrintFileMetadataState.Unread, "the backfill reads it again");
        row.PrinterModel.Should().BeNull("the compatibility check would hold a queue on the old file's model");
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

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
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

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

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
    /// A row whose file was renamed on disk to another case, while the service was stopped, takes the
    /// disk's spelling - so a later lookup by that spelling finds it rather than indexing a second row.
    /// </summary>
    [Fact]
    public async Task ARowTakesTheSpellingOfItsFileOnDisk()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);

        string path = await WriteFileAsync("ærø.gcode", [1, 2, 3]);
        await AddRowFromDiskAsync(context, "ærø.gcode", "uploaded");
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "Ærø.gcode"));

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Name.Should().Be("Ærø.gcode");
        row.Digest.Should().Be("uploaded", "a rename does not change the bytes");
    }

    /// <summary>
    /// Two rows for one file - which a database written before rows were matched by the store's rule
    /// can hold - are reported and left alone, not a reason to stop reconciling every other user.
    /// </summary>
    [Fact]
    public async Task TwoRowsForOneFileDoNotStopTheReconcileForAnyoneElse()
    {
        // Arrange
        const long bob = 2;

        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await AddUserAsync(context, bob, "bob@example.com");

        await WriteFileAsync("Ærø.gcode", [1, 2, 3]);

        foreach (string name in new[] { "ærø.gcode", "Ærø.gcode" })
        {
            context.Files.Add(new HSFile
            {
                Type = FileType.GCode,
                UserId = Alice,
                Name = name,
                Size = 3,
                UploadedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(_root, "2-bob"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "2-bob", "handcopied.gcode"), [1, 2, 3],
                                      TestContext.Current.CancellationToken);

        FakeLogger<PrintFileReconciler> logger = new();

        // Act
        using PrintFileReconciler reconciler = NewReconciler(logger);
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        (await context.Files.CountAsync(row => row.UserId == Alice, TestContext.Current.CancellationToken))
            .Should().Be(2, "neither row is the reconcile's to remove");
        (await context.Files.SingleAsync(row => row.UserId == bob, TestContext.Current.CancellationToken))
            .Name.Should().Be("handcopied.gcode");
        logger.Collector.GetSnapshot().Should().ContainSingle(record => record.Level == LogLevel.Error);
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
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        File.Exists(path).Should().BeTrue("the reconciler never writes to the disk");
    }

    /// <summary>
    /// A firmware image's bytes are in another store, so its row is not a file this walk lost: it
    /// survives a reconcile of a directory that does not hold it.
    /// </summary>
    [Fact]
    public async Task AFirmwareImageIsNotAFileThePrintStoreLost()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await WriteFileAsync("present.gcode", [1, 2, 3]);
        await AddFirmwareRowAsync(context, size: 3, uploadedAt: DateTimeOffset.UnixEpoch);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        (await context.Files.SingleAsync(row => row.Type == FileType.PrusaFirmware, TestContext.Current.CancellationToken))
            .Digest.Should().Be("firmware");
    }

    /// <summary>
    /// A <c>.bbf</c> copied by hand into a print directory is indexed as that user's file, beside the
    /// firmware image of the same name rather than as a correction to it.
    /// </summary>
    [Fact]
    public async Task AHandCopiedBbfIsIndexedBesideTheFirmwareImageOfTheSameName()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await WriteFileAsync(FirmwareImageName, [1, 2, 3, 4]);
        await AddFirmwareRowAsync(context, size: 3, uploadedAt: DateTimeOffset.UnixEpoch);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        List<HSFile> rows = await context.Files.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        rows[0].Type.Should().Be(FileType.PrusaFirmware);
        rows[0].Size.Should().Be(3, "the image's row describes the image, not the copy");
        rows[0].Digest.Should().Be("firmware");
        rows[1].Type.Should().Be(FileType.GCode);
        rows[1].Size.Should().Be(4);
    }

    /// <summary>
    /// The running recheck corrects G-code rows only: a hand-copied file of a firmware image's name,
    /// with other bytes, is no statement about the image.
    /// </summary>
    [Fact]
    public async Task TheRecheckLeavesAFirmwareImageAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await WriteFileAsync(FirmwareImageName, [1, 2, 3, 4]);
        await AddFirmwareRowAsync(context, size: 3, uploadedAt: DateTimeOffset.UnixEpoch);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.RecheckAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile image = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        image.Size.Should().Be(3);
        image.Digest.Should().Be("firmware");
    }

    /// <summary>
    /// The backfill reads G-code rows only: it never hashes or describes a firmware image from a file
    /// in somebody's print directory, even one that matches the image's row exactly.
    /// </summary>
    [Fact]
    public async Task TheBackfillLeavesAFirmwareImageAlone()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await WriteFileAsync(FirmwareImageName, [1, 2, 3]);

        StoredFile copy = NewStore().Find(Alice, FirmwareImageName)!;
        await AddFirmwareRowAsync(context, copy.Length, copy.UploadedAt, digest: null);

        // Act
        using PrintFileReconciler reconciler = NewReconciler();
        await reconciler.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile image = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        image.Digest.Should().BeNull();
        image.MetadataState.Should().Be(PrintFileMetadataState.Undefined);
    }

    private static async Task AddUserAsync(HomespoolDbContext context, long id = Alice, string email = "alice@example.com")
    {
        context.Users.Add(new HSUser(email)
        {
            Id = id,
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
    /// <remarks>
    /// The metadata state defaults to <see cref="PrintFileMetadataState.Read"/>, as an upload leaves it,
    /// so a test about digests is not also a test about descriptions.
    /// </remarks>
    private async Task AddRowFromDiskAsync(HomespoolDbContext context,
                                           string name,
                                           string? digest,
                                           PrintFileMetadataState metadataState = PrintFileMetadataState.Read,
                                           long sizeOffset = 0,
                                           string? printerModel = null)
    {
        StoredFile stored = NewStore().Find(Alice, name)!;

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
            UserId = Alice,
            Name = stored.FileName,
            Size = stored.Length + sizeOffset,
            Digest = digest,
            UploadedAt = stored.UploadedAt,
            MetadataState = metadataState,
            PrinterModel = printerModel,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A firmware image owned by Alice, as the firmware store would index it.</summary>
    private static async Task AddFirmwareRowAsync(HomespoolDbContext context,
                                                  long size,
                                                  DateTimeOffset uploadedAt,
                                                  string? digest = "firmware")
    {
        context.Files.Add(new HSFile
        {
            Type = FileType.PrusaFirmware,
            UserId = Alice,
            Name = FirmwareImageName,
            Size = size,
            Digest = digest,
            UploadedAt = uploadedAt,
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
            digest = await context.Files
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
