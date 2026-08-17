using System;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream(content), overwrite: false,
                                TestContext.Current.CancellationToken);

        // Assert
        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

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

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        long queuedPrintId = await AddQueuedPrintAsync(context, row.Id);

        // Act
        await catalog.RenameAsync(Caller.Unscoped(Alice), "benchy.gcode", "boat.gcode", TestContext.Current.CancellationToken);

        // Assert
        PrintFile renamed = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        renamed.Id.Should().Be(row.Id, "the row is the identity a queue entry points at");
        renamed.Name.Should().Be("boat.gcode");

        QueuedPrint job = await context.QueuedPrints.SingleAsync(j => j.Id == queuedPrintId,
                                                                 TestContext.Current.CancellationToken);

        job.PrintFileId.Should().Be(row.Id);
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

        long originalId = (await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken)).Id;

        // Act
        await catalog.SaveAsync(Caller.Unscoped(Alice), "benchy.gcode", new MemoryStream(replacement), overwrite: true,
                                TestContext.Current.CancellationToken);

        // Assert
        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

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

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        await AddQueuedPrintAsync(context, row.Id);

        // Act
        PrintFileDeletion result =
            await catalog.DeleteAsync(Caller.Unscoped(Alice), "benchy.gcode", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Queued);
        catalog.Find(Caller.Unscoped(Alice), "benchy.gcode").Should().NotBeNull("refusing must not half-delete the file");
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
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
            await catalog.DeleteAsync(Caller.Unscoped(Alice), "benchy.gcode", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Deleted);
        catalog.Find(Caller.Unscoped(Alice), "benchy.gcode").Should().BeNull();
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
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
        PrintFile? row = await catalog.ResolveAsync(Alice, "orphan.gcode", TestContext.Current.CancellationToken);

        // Assert
        row.Should().NotBeNull();
        row!.Name.Should().Be("orphan.gcode");
        row.Digest.Should().BeNull("resolving does not read the file to hash it");
    }

    [Fact]
    public async Task ResolvingAFileThatIsNotThereIsNull()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        // Act
        PrintFile? row = await catalog.ResolveAsync(Alice, "nothing.gcode", TestContext.Current.CancellationToken);

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
            PrintFileId = printFileId,
            Position = 0,
            QueuedByUserId = Alice,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UnixEpoch,
        };

        context.QueuedPrints.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return job.Id;
    }

    private UserFileStore NewStore()
    {
        return new(Options.Create(new PrintFileStorageOptions { Directory = _root }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<UserFileStore>.Instance);
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

        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

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
        PrintFile? row = await catalog.ResolveAsync(Alice, "benchy.gcode", TestContext.Current.CancellationToken);

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

        Caller uploader = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.UploadOwnFiles])));

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

        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

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

        Caller uploader = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.UploadOwnFiles])));

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
