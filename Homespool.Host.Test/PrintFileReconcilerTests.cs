using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private PrintFileReconciler NewReconciler()
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

        UserFileStore store = new(Options.Create(new PrintFileStorageOptions { Directory = _root }),
                                  new HostEnvironmentAccessor(_root),
                                  TimeProvider.System,
                                  NullLogger<UserFileStore>.Instance);

        return new PrintFileReconciler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                                       store,
                                       Options.Create(new PrintFileStorageOptions { Directory = _root }),
                                       new HostEnvironmentAccessor(_root),
                                       NullLogger<PrintFileReconciler>.Instance);
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
