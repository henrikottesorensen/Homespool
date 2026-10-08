using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintFileCatalog"/> - the store and its index kept in step, and the one delete it
/// refuses.
/// </summary>
/// <remarks>
/// Real SQLite rather than the in-memory provider, for the same reason the credential suites use it:
/// the <c>NOCASE</c> unique index and the <c>Restrict</c> foreign key that backs the refusal are
/// database behaviour, and a provider that fakes both would let a broken schema pass.
/// </remarks>
public sealed class PrintFileCatalogTests : IDisposable
{
    private const long Alice = 1;

    private const string FirmwareImageName = "COREONE_firmware_7.0.0.bbf";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-catalog-{Guid.NewGuid():N}.db");

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
    /// The digest is SHA-384 of the content, base64url - pinned as a value rather than described, so
    /// that changing the algorithm is a failing test rather than a silent change of meaning.
    /// </summary>
    [Fact]
    public async Task AnUploadRecordsTheSha384OfItsContent()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);
        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n");

        // Act
        await catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.UploadOwnFiles), "benchy.gcode", new MemoryStream(content),
                                overwrite: false, TestContext.Current.CancellationToken);

        // Assert
        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be(Base64Url.EncodeToString(SHA384.HashData(content)));
        row.Name.Should().Be("benchy.gcode");
        row.Size.Should().Be(content.Length);
    }

    /// <summary>
    /// The point of the whole table: a rename moves the file without disturbing what references it.
    /// </summary>
    [Fact]
    public async Task RenamingKeepsTheRowSoAQueuedPrintSurvivesIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                                TestContext.Current.CancellationToken);

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);
        long queuedPrintId = await AddQueuedPrintAsync(context, row.Id);

        // Act
        await catalog.RenameAsync(TestCallers.Scoped(Alice, Capability.ManipulateOwnFiles), "benchy.gcode", "boat.gcode",
                                  TestContext.Current.CancellationToken);

        // Assert
        HSFile renamed = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        renamed.Id.Should().Be(row.Id, "the row is the identity a queue entry points at");
        renamed.Name.Should().Be("boat.gcode");

        QueuedPrint job = await context.QueuedPrints.SingleAsync(j => j.Id == queuedPrintId,
                                                                 TestContext.Current.CancellationToken);

        job.FileId.Should().Be(row.Id);
    }

    /// <summary>
    /// Overwriting replaces the content under the same identity, so a job queued before the re-slice
    /// prints the new bytes - and the digest has to move with them.
    /// </summary>
    [Fact]
    public async Task OverwritingKeepsTheRowAndUpdatesTheDigest()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);
        byte[] replacement = Encoding.UTF8.GetBytes("second");

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream(Encoding.UTF8.GetBytes("first")),
                                overwrite: false, TestContext.Current.CancellationToken);

        long originalId = (await context.Files.SingleAsync(TestContext.Current.CancellationToken)).Id;

        // Act
        await catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.ManipulateOwnFiles), "benchy.gcode",
                                new MemoryStream(replacement), overwrite: true, TestContext.Current.CancellationToken);

        // Assert
        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Id.Should().Be(originalId);
        row.Digest.Should().Be(Base64Url.EncodeToString(SHA384.HashData(replacement)));
    }

    /// <summary>
    /// The refusal that stops one person tidying up their files from silently cancelling somebody
    /// else's queued print.
    /// </summary>
    [Fact]
    public async Task DeletingAFileAQueuedPrintWantsIsRefusedAndLeavesItOnDisk()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                                TestContext.Current.CancellationToken);

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);
        await AddQueuedPrintAsync(context, row.Id);

        // Act
        PrintFileDeletion result =
            await catalog.DeleteAsync(TestCallers.Scoped(Alice, Capability.ManipulateOwnFiles), "benchy.gcode",
                                      TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Queued);
        catalog.Find(Caller.Unscoped(Alice), "benchy.gcode").Should().NotBeNull("refusing must not half-delete the file");
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>The ordinary delete still takes both halves.</summary>
    [Fact]
    public async Task DeletingAnUnqueuedFileRemovesTheFileAndItsRow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                                TestContext.Current.CancellationToken);

        // Act
        PrintFileDeletion result =
            await catalog.DeleteAsync(TestCallers.Scoped(Alice, Capability.ManipulateOwnFiles), "benchy.gcode",
                                      TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Deleted);
        catalog.Find(Caller.Unscoped(Alice), "benchy.gcode").Should().BeNull();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A file that predates the table is still queueable: resolving indexes it on the spot rather than
    /// reporting an implementation detail as a missing file.
    /// </summary>
    [Fact]
    public async Task ResolvingIndexesAFileThatHasNoRowYet()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        // Straight to the store, so the row is never written - a file from before this table existed.
        await store.SaveAsync(Alice, "orphan.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                              TestContext.Current.CancellationToken);

        // Act
        HSFile? row = await catalog.ResolveAsync(Alice, "orphan.gcode", TestContext.Current.CancellationToken);

        // Assert
        row.Should().NotBeNull();
        row!.Name.Should().Be("orphan.gcode");
        row.Digest.Should().BeNull("resolving does not read the file to hash it");
        row.MetadataState.Should().Be(PrintFileMetadataState.Unread, "nor to read its metadata");
    }

    /// <summary>
    /// A file with no digest yet is read for one before it is sent, and the row keeps it, so the next
    /// send does not read it again.
    /// </summary>
    [Fact]
    public async Task SendingAFileWithNoDigestReadsOneAndKeepsIt()
    {
        // Arrange - indexed on the way to a print, so the row has no digest
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewStore();
        PrintFileCatalog catalog = NewCatalog(context, store);
        byte[] content = [1, 2, 3];

        await store.SaveAsync(Alice, "orphan.gcode", new MemoryStream(content), overwrite: false,
                              TestContext.Current.CancellationToken);
        HSFile row = (await catalog.ResolveAsync(Alice, "orphan.gcode", TestContext.Current.CancellationToken))!;

        // Act
        string digest = await catalog.DigestForSendingAsync(row, store.Find(Alice, "orphan.gcode")!,
                                                            TestContext.Current.CancellationToken);

        // Assert
        digest.Should().Be(Base64Url.EncodeToString(SHA384.HashData(content)), "the same digest an upload records");

        context.ChangeTracker.Clear();
        (await context.Files.SingleAsync(TestContext.Current.CancellationToken)).Digest.Should().Be(digest);
    }

    /// <summary>
    /// A row that no longer describes the bytes that were read - replaced meanwhile - is not given
    /// their digest, though the send still is.
    /// </summary>
    [Fact]
    public async Task SendingDoesNotWriteADigestOverARowThatMovedOn()
    {
        // Arrange - the row says another size than the bytes on disk
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        await store.SaveAsync(Alice, "orphan.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                              TestContext.Current.CancellationToken);
        HSFile row = (await catalog.ResolveAsync(Alice, "orphan.gcode", TestContext.Current.CancellationToken))!;

        await context.Files.ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.Size, 4096),
                                               TestContext.Current.CancellationToken);

        // Act
        string digest = await catalog.DigestForSendingAsync(row, store.Find(Alice, "orphan.gcode")!,
                                                            TestContext.Current.CancellationToken);

        // Assert
        digest.Should().NotBeNullOrEmpty("the bytes about to be sent are what it describes");

        context.ChangeTracker.Clear();
        (await context.Files.SingleAsync(TestContext.Current.CancellationToken)).Digest
            .Should().BeNull("the row describes other bytes now");
    }

    /// <summary>A digest the row already has is the answer, and the file is not read for it.</summary>
    [Fact]
    public async Task SendingAFileWithADigestDoesNotReadIt()
    {
        // Arrange - a row with a digest, and the file gone, so a read would throw
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        await store.SaveAsync(Alice, "kept.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                              TestContext.Current.CancellationToken);
        StoredFile file = store.Find(Alice, "kept.gcode")!;
        File.Delete(file.Path);

        HSFile row = new() { Type = FileType.GCode, UserId = Alice, Name = "kept.gcode", Digest = "known" };

        // Act
        string digest = await catalog.DigestForSendingAsync(row, file, TestContext.Current.CancellationToken);

        // Assert
        digest.Should().Be("known");
    }

    /// <summary>
    /// An overwrite spelled with a different case of a non-ASCII letter replaces the one file, so it
    /// must keep the one row. SQLite's <c>NOCASE</c> folds ASCII only, so a row lookup it answered
    /// missed <c>ærø</c> for <c>Ærø</c> and inserted a second row beside the first.
    /// </summary>
    [Fact]
    public async Task OverwritingUnderAnotherCaseOfANonAsciiLetterKeepsTheOneRow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "ærø.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                                TestContext.Current.CancellationToken);

        HSFile original = await context.Files.SingleAsync(TestContext.Current.CancellationToken);
        long queuedPrintId = await AddQueuedPrintAsync(context, original.Id);

        // Act
        await catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.ManipulateOwnFiles), "Ærø.gcode",
                                new MemoryStream([4, 5, 6, 7]), overwrite: true, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);

        row.Id.Should().Be(original.Id, "the queued print points at this row");
        row.Name.Should().Be("Ærø.gcode", "a row carries the spelling on disk");
        row.Size.Should().Be(4);
        (await context.QueuedPrints.SingleAsync(job => job.Id == queuedPrintId, TestContext.Current.CancellationToken))
            .FileId.Should().Be(original.Id);
    }

    /// <summary>
    /// A file renamed on disk by hand to another case is still the same file: resolving it finds its
    /// row, rather than indexing a second one, and the row takes the new spelling.
    /// </summary>
    [Fact]
    public async Task ResolvingAFileRenamedOnDiskToAnotherCaseFindsItsRow()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        StoredFile saved = await catalog.SaveAsync(Caller.Unscoped(Alice), "ærø.gcode", new MemoryStream([1, 2, 3]),
                                                   overwrite: false, TestContext.Current.CancellationToken);

        long originalId = (await context.Files.SingleAsync(TestContext.Current.CancellationToken)).Id;
        File.Move(saved.Path, Path.Combine(Path.GetDirectoryName(saved.Path)!, "Ærø.gcode"));

        // Act
        HSFile? row = await catalog.ResolveAsync(Alice, "Ærø.gcode", TestContext.Current.CancellationToken);

        // Assert
        row.Should().NotBeNull();
        row!.Id.Should().Be(originalId);

        context.ChangeTracker.Clear();

        (await context.Files.SingleAsync(TestContext.Current.CancellationToken)).Name.Should().Be("Ærø.gcode");
    }

    /// <summary>
    /// Two rows for one file - which only a database from before rows were matched by the store's rule
    /// can hold - still list: the file is described by the row spelled as the disk is, whichever is
    /// older.
    /// </summary>
    [Fact]
    public async Task ListingAFileWithTwoRowsDescribesItByTheOneSpelledAsTheDisk()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "Ærø.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
                                TestContext.Current.CancellationToken);

        HSFile exact = await context.Files.SingleAsync(TestContext.Current.CancellationToken);
        exact.Name = "ærø.gcode";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
            UserId = Alice,
            Name = "Ærø.gcode",
            Size = 3,
            UploadedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<CataloguedFile> listed = await catalog.ListAsync(TestCallers.Scoped(Alice, Capability.ViewOwnFiles),
                                                                       TestContext.Current.CancellationToken);

        // Assert
        listed.Should().ContainSingle().Which.Row!.Name.Should().Be("Ærø.gcode");
    }

    [Fact]
    public async Task ResolvingAFileThatIsNotThereIsNull()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        // Act
        HSFile? row = await catalog.ResolveAsync(Alice, "nothing.gcode", TestContext.Current.CancellationToken);

        // Assert
        row.Should().BeNull();
    }

    private static async Task<HSUser> AddUserAsync(HomespoolDbContext context, string email = "alice@example.com")
    {
        HSUser user = new(email)
        {
            Id = Alice,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    /// <summary>A queue entry pointing at a file, with the printer and team it needs to exist.</summary>
    private static async Task<long> AddQueuedPrintAsync(HomespoolDbContext context, long printFileId)
    {
        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        QueuedPrint job = new()
        {
            PrinterId = printer.Id,
            FileId = printFileId,
            Position = 0,
            QueuedByUserId = Alice,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UnixEpoch,
        };

        context.QueuedPrints.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return job.Id;
    }

    private UserFileStore NewUnconfirmedStore()
    {
        return new(TestOptions.Monitor(new PrintFileStorageOptions { Directory = _root }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<UserFileStore>.Instance);
    }

    private UserFileStore NewStore()
    {
        UserFileStore store = new(TestOptions.Monitor(new PrintFileStorageOptions { Directory = _root }),
                                  new HostEnvironmentAccessor(_root),
                                  TimeProvider.System,
                                  NullLogger<UserFileStore>.Instance);

        // A store an operator has already confirmed, which is what every install but a fresh one has.
        store.Confirm();

        return store;
    }

    /// <summary>
    /// A fresh install has nothing indexed, so nothing an empty root could be hiding: the first
    /// upload marks the storage itself, and needs no operator.
    /// </summary>
    [Fact]
    public async Task TheFirstUploadOfAFreshInstallMarksTheStorage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        UserFileStore store = NewUnconfirmedStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        // Act
        StoredFile saved = await catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.UploadOwnFiles), "first.gcode", new MemoryStream([1]),
                                                   overwrite: false, TestContext.Current.CancellationToken);

        // Assert
        saved.FileName.Should().Be("first.gcode");
        store.IsConfirmed.Should().BeTrue();
    }

    /// <summary>
    /// <b>Rows with no marker is what an unmounted volume looks like</b>, so an install that has rows
    /// is never marked by the app: an upload is refused until the operator creates the file.
    /// </summary>
    [Fact]
    public async Task AnInstallWithIndexedFilesIsNotMarkedByAnUpload()
    {
        // Arrange - one file indexed, and a root with no marker
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        context.Files.Add(new HSFile
        {
            Type = FileType.GCode,
            UserId = Alice,
            Name = "old.gcode",
            Size = 1,
            UploadedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(_root);
        UserFileStore store = NewUnconfirmedStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        // Act
        Func<Task> upload = () => catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.UploadOwnFiles), "new.gcode", new MemoryStream([1]),
                                                    overwrite: false, TestContext.Current.CancellationToken);

        // Assert
        await upload.Should().ThrowAsync<PrintFileStorageUnconfirmedException>();
        store.IsConfirmed.Should().BeFalse();
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    /// <summary>
    /// Only a G-code row says the users' storage has held something, so a firmware image uploaded
    /// before any print file leaves a fresh install free to mark its storage on the first upload.
    /// </summary>
    [Fact]
    public async Task AFirmwareImageAloneDoesNotStopAFreshInstallMarkingTheStorage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        await AddFirmwareRowAsync(context);

        UserFileStore store = NewUnconfirmedStore();
        PrintFileCatalog catalog = NewCatalog(context, store);

        // Act
        await catalog.SaveAsync(TestCallers.Scoped(Alice, Capability.UploadOwnFiles), "first.gcode", new MemoryStream([1]),
                                overwrite: false, TestContext.Current.CancellationToken);

        // Assert
        store.IsConfirmed.Should().BeTrue();
    }

    /// <summary>
    /// A <c>.bbf</c> copied by hand into a print directory is that user's file like any other, and
    /// resolving it indexes a G-code row of its own - never the firmware image of the same name.
    /// </summary>
    [Fact]
    public async Task AHandCopiedBbfResolvesToItsOwnRowNotTheFirmwareImage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        HSFile image = await AddFirmwareRowAsync(context);
        await CopyIntoAlicesDirectoryAsync(catalog, FirmwareImageName);

        // Act
        HSFile? row = await catalog.ResolveAsync(Alice, FirmwareImageName, TestContext.Current.CancellationToken);
        HSFile? again = await catalog.ResolveAsync(Alice, FirmwareImageName, TestContext.Current.CancellationToken);

        // Assert
        row.Should().NotBeNull();
        row!.Type.Should().Be(FileType.GCode);
        row.Id.Should().NotBe(image.Id, "the firmware image is not anybody's print");
        again!.Id.Should().Be(row.Id, "with both rows indexed, the name still finds the user's own");
    }

    /// <summary>
    /// The listing matches files to G-code rows only: a hand-copied <c>.bbf</c> with no row of its own
    /// is listed with nothing known about it, not described by the firmware image of the same name.
    /// </summary>
    [Fact]
    public async Task ListingNeverDescribesAFileByAFirmwareImage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await AddFirmwareRowAsync(context);
        await CopyIntoAlicesDirectoryAsync(catalog, FirmwareImageName);

        // Act
        IReadOnlyList<CataloguedFile> listed = await catalog.ListAsync(TestCallers.Scoped(Alice, Capability.ViewOwnFiles),
                                                                       TestContext.Current.CancellationToken);

        // Assert
        listed.Should().ContainSingle(file => file.File.FileName == FirmwareImageName)
              .Which.Row.Should().BeNull();
    }

    /// <summary>A firmware image owned by Alice, as the firmware store would index it.</summary>
    private static async Task<HSFile> AddFirmwareRowAsync(HomespoolDbContext context)
    {
        HSFile image = new()
        {
            Type = FileType.PrusaFirmware,
            UserId = Alice,
            Name = FirmwareImageName,
            Size = 3,
            Digest = "firmware",
            UploadedAt = DateTimeOffset.UnixEpoch,
        };

        context.Files.Add(image);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return image;
    }

    /// <summary>
    /// Puts a file in Alice's directory without going through the catalogue, which would refuse the
    /// extension - creating the directory with an upload first.
    /// </summary>
    private static async Task CopyIntoAlicesDirectoryAsync(PrintFileCatalog catalog, string name)
    {
        StoredFile anchor = await catalog.SaveAsync(Caller.Unscoped(Alice), "anchor.gcode", new MemoryStream([1]),
                                                    overwrite: false, TestContext.Current.CancellationToken);

        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(anchor.Path)!, name), [1, 2, 3],
                                      TestContext.Current.CancellationToken);
    }

    private PrintFileCatalog NewCatalog(HomespoolDbContext context, UserFileStore? store = null)
    {
        return new(store ?? NewStore(), context, NullLogger<PrintFileCatalog>.Instance);
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    /// <summary>
    /// <b>The case that makes scoped tokens honest.</b> A token scoped to one printer's work still
    /// reaches every file its owner has unless the file surface asks the credential - so a caller
    /// holding <c>Print</c> and nothing file-shaped is refused each file operation in turn.
    /// </summary>
    [Fact]
    public async Task ACallerScopedToPrintingCannotTouchTheFileSurface()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", Content(), overwrite: false,
                                TestContext.Current.CancellationToken);

        Caller printing = TestCallers.Scoped(Alice, Capability.Print);

        // Act & Assert
        FluentActions.Invoking(() => catalog.List(printing))
                     .Should().Throw<CredentialScopeDeniedException>("listing is ViewOwnFiles");

        FluentActions.Invoking(() => catalog.Find(printing, "benchy.gcode"))
                     .Should().Throw<CredentialScopeDeniedException>("downloading is ViewOwnFiles");

        await FluentActions.Awaiting(() => catalog.SaveAsync(printing, "other.gcode", Content(), overwrite: false,
                                                             TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<CredentialScopeDeniedException>("uploading is UploadOwnFiles");

        await FluentActions.Awaiting(() => catalog.RenameAsync(printing, "benchy.gcode", "renamed.gcode",
                                                               TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<CredentialScopeDeniedException>("renaming is ManipulateOwnFiles");

        await FluentActions.Awaiting(() => catalog.DeleteAsync(printing, "benchy.gcode",
                                                               TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<CredentialScopeDeniedException>("deleting is ManipulateOwnFiles");
    }

    /// <summary>
    /// <b>And it can still print.</b> Resolving the bytes to send is part of printing, not a
    /// browsing-shaped permission - so the same credential that cannot list finds its own file to
    /// queue. A gate here would mean a token scoped to print could not print.
    /// </summary>
    [Fact]
    public async Task ACallerScopedToPrintingCanStillResolveItsOwnFileToPrint()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", Content(), overwrite: false,
                                TestContext.Current.CancellationToken);

        // Act
        StoredFile? resolved = catalog.FindForPrinting(Alice, "benchy.gcode");
        HSFile? row = await catalog.ResolveAsync(Alice, "benchy.gcode", TestContext.Current.CancellationToken);

        // Assert
        resolved.Should().NotBeNull();
        row.Should().NotBeNull();
    }

    /// <summary>
    /// <b>Overwriting is manipulation, not uploading.</b> A credential holding only
    /// <c>UploadOwnFiles</c> writes a new name and is refused one that exists.
    /// </summary>
    [Fact]
    public async Task UploadingCoversANewNameButNotReplacingAnExistingOne()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        Caller uploader = TestCallers.Scoped(Alice, Capability.UploadOwnFiles);

        // Act
        await catalog.SaveAsync(uploader, "benchy.gcode", Content(), overwrite: false,
                                TestContext.Current.CancellationToken);

        // Assert
        await FluentActions.Awaiting(() => catalog.SaveAsync(uploader, "benchy.gcode", Content(), overwrite: true,
                                                             TestContext.Current.CancellationToken))
                           .Should()
                           .ThrowAsync<CredentialScopeDeniedException>(
                               "replacing bytes under a name in use is ManipulateOwnFiles");
    }

    /// <summary>An ordinary session narrows nothing, so the whole file surface stays open to it.</summary>
    [Fact]
    public async Task AnUnscopedCallerIsRefusedNothingOnItsOwnFiles()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        Caller session = Caller.Unscoped(Alice);

        // Act
        await catalog.SaveAsync(session, "benchy.gcode", Content(), overwrite: false,
                                TestContext.Current.CancellationToken);

        // Assert
        catalog.List(session).Should().ContainSingle();
        catalog.Find(session, "benchy.gcode").Should().NotBeNull();

        (await catalog.RenameAsync(session, "benchy.gcode", "renamed.gcode", TestContext.Current.CancellationToken))
            .Should().NotBeNull();

        (await catalog.DeleteAsync(session, "renamed.gcode", TestContext.Current.CancellationToken))
            .Should().Be(PrintFileDeletion.Deleted);
    }

    /// <summary>
    /// <b>The staged upload path the browser uses, which the API's single-shot save does not touch.</b>
    /// Staging writes bytes and publishing names them, so both are uploading and both ask the
    /// credential - otherwise a scoped token could upload through the page's route while being
    /// refused the controller's.
    /// </summary>
    [Fact]
    public async Task StagingAndDiscardingAnUploadAskTheCredentialToo()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        Caller printing = TestCallers.Scoped(Alice, Capability.Print);

        // Act & Assert
        await FluentActions.Awaiting(() => catalog.StageAsync(printing, "benchy.gcode", Content(),
                                                              TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<CredentialScopeDeniedException>("staging bytes is UploadOwnFiles");

        FluentActions.Invoking(() => catalog.Discard(printing, "any-token"))
                     .Should()
                     .Throw<CredentialScopeDeniedException>("throwing away your own staged upload is uploading too");
    }

    /// <summary>Publishing a staged upload under a name in use is manipulation, as a direct save is.</summary>
    [Fact]
    public async Task PublishingOverAnExistingNameNeedsManipulate()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        Caller uploader = TestCallers.Scoped(Alice, Capability.UploadOwnFiles);

        PendingUpload staged = await catalog.StageAsync(uploader, "benchy.gcode", Content(),
                                                        TestContext.Current.CancellationToken);

        // Act & Assert
        await FluentActions.Awaiting(() => catalog.PublishAsync(uploader, staged.Token, overwrite: true,
                                                                TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<CredentialScopeDeniedException>();

        (await catalog.PublishAsync(uploader, staged.Token, overwrite: false, TestContext.Current.CancellationToken))
            .Should().NotBeNull("a new name is what UploadOwnFiles is for");
    }

    private static MemoryStream Content()
    {
        return new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n"));
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
