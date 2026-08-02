using System;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrintFiles;
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
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);
        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n");

        // Act
        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream(content), overwrite: false,
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
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
            TestContext.Current.CancellationToken);

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        long queuedPrintId = await AddQueuedPrintAsync(context, row.Id);

        // Act
        await catalog.RenameAsync(Alice, "benchy.gcode", "boat.gcode", TestContext.Current.CancellationToken);

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
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);
        byte[] replacement = Encoding.UTF8.GetBytes("second");

        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream(Encoding.UTF8.GetBytes("first")),
            overwrite: false, TestContext.Current.CancellationToken);

        long originalId = (await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken)).Id;

        // Act
        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream(replacement), overwrite: true,
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
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
            TestContext.Current.CancellationToken);

        PrintFile row = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        await AddQueuedPrintAsync(context, row.Id);

        // Act
        PrintFileDeletion result =
            await catalog.DeleteAsync(Alice, "benchy.gcode", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Queued);
        catalog.Find(Alice, "benchy.gcode").Should().NotBeNull("refusing must not half-delete the file");
        (await context.PrintFiles.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>The ordinary delete still takes both halves.</summary>
    [Fact]
    public async Task DeletingAnUnqueuedFileRemovesTheFileAndItsRow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        await catalog.SaveAsync(Alice, "benchy.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
            TestContext.Current.CancellationToken);

        // Act
        PrintFileDeletion result =
            await catalog.DeleteAsync(Alice, "benchy.gcode", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(PrintFileDeletion.Deleted);
        catalog.Find(Alice, "benchy.gcode").Should().BeNull();
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
        await using HSDbContext context = await MigratedContextAsync();
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
        await using HSDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        PrintFileCatalog catalog = NewCatalog(context);

        // Act
        PrintFile? row = await catalog.ResolveAsync(Alice, "nothing.gcode", TestContext.Current.CancellationToken);

        // Assert
        row.Should().BeNull();
    }

    private static async Task<HSUser> AddUserAsync(HSDbContext context, string email = "alice@example.com")
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
    private static async Task<long> AddQueuedPrintAsync(HSDbContext context, long printFileId)
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

    private PrintFileCatalog NewCatalog(HSDbContext context, UserFileStore? store = null)
    {
        return new(store ?? NewStore(), context, NullLogger<PrintFileCatalog>.Instance);
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
