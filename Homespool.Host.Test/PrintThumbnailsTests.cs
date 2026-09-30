using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Which file a print's preview comes from, when it is refused, and that it is read once per print.
/// </summary>
public sealed class PrintThumbnailsTests : IAsyncLifetime
{
    private const long Alice = 1;
    private const long Bob = 2;

    /// <summary>A real 1x1 PNG, tagged after its end so two can be told apart.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-thumbnails-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-thumbnails-{Guid.NewGuid():N}.db");

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddSingleton(new UserFileStore(TestOptions.Monitor(new PrintFileStorageOptions { Directory = _root }),
                                                new HostEnvironmentAccessor(_root),
                                                TimeProvider.System,
                                                NullLogger<UserFileStore>.Instance));
        services.AddScoped<PrintFileCatalog>();
        services.AddSingleton<PrintThumbnails>();

        _services = services.BuildServiceProvider();

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach ((long id, string name) in new[] { (Alice, "alice"), (Bob, "bob") })
        {
            context.Users.Add(new HSUser(name)
            {
                Id = id,
                Email = $"{name}@example.com",
                NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.COM",
                NormalizedUserName = name.ToUpperInvariant(),
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

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
    }

    /// <summary>The ordinary case: the file that was sent, unchanged, and its preview.</summary>
    [Fact]
    public async Task APrintShowsThePreviewOfTheFileItWasSentFrom()
    {
        string digest = await UploadAsync(Alice, "cube.gcode", Png(1));

        byte[]? image = await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest), TestContext.Current.CancellationToken);

        image.Should().Equal(Png(1));
    }

    /// <summary>
    /// Two people with a file of the same name: the preview is of the one who queued the print. Looking
    /// in anybody else's files would show a picture of something that is not printing.
    /// </summary>
    [Fact]
    public async Task ThePreviewComesFromTheFilesOfWhoeverQueuedThePrint()
    {
        await UploadAsync(Bob, "cube.gcode", Png(2));
        string digest = await UploadAsync(Alice, "cube.gcode", Png(1));

        byte[]? image = await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest), TestContext.Current.CancellationToken);

        image.Should().Equal(Png(1));
    }

    /// <summary>
    /// Overwritten since the print opened: the name still finds a file, and it is not the one that is
    /// printing, so there is no preview rather than the wrong one.
    /// </summary>
    [Fact]
    public async Task AFileChangedSinceThePrintOpenedHasNoPreview()
    {
        string printed = await UploadAsync(Alice, "cube.gcode", Png(1));
        await UploadAsync(Alice, "cube.gcode", Png(2), overwrite: true);

        byte[]? image = await Thumbnails.ForAsync(Job(Alice, "cube.gcode", printed), TestContext.Current.CancellationToken);

        image.Should().BeNull();
    }

    /// <summary>
    /// A print that opened before its file had a digest has nothing to compare, and takes the file as
    /// it is - the same rule a reprint follows.
    /// </summary>
    [Fact]
    public async Task APrintWithNoDigestTakesTheFileAsItIs()
    {
        await UploadAsync(Alice, "cube.gcode", Png(1));

        byte[]? image = await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest: null), TestContext.Current.CancellationToken);

        image.Should().Equal(Png(1));
    }

    /// <summary>A file that has gone since has no preview.</summary>
    [Fact]
    public async Task AFileThatIsGoneHasNoPreview()
    {
        byte[]? image = await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest: null), TestContext.Current.CancellationToken);

        image.Should().BeNull();
    }

    /// <summary>
    /// Read once per print. The status card asks every two seconds, and the picture of a print does
    /// not change even when its file does afterwards - so the second answer is the first one, and a
    /// different print reads again.
    /// </summary>
    [Fact]
    public async Task APreviewIsReadOncePerPrint()
    {
        await UploadAsync(Alice, "cube.gcode", Png(1));
        PrintJob first = Job(Alice, "cube.gcode", digest: null);

        await Thumbnails.ForAsync(first, TestContext.Current.CancellationToken);
        await UploadAsync(Alice, "cube.gcode", Png(2), overwrite: true);

        (await Thumbnails.ForAsync(first, TestContext.Current.CancellationToken)).Should().Equal(Png(1));
        (await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest: null), TestContext.Current.CancellationToken))
            .Should().Equal(Png(2));
    }

    /// <summary>
    /// Viewers and polls arriving at once share one read rather than each starting their own - every
    /// one of them is handed the very same array.
    /// </summary>
    [Fact]
    public async Task CallersAskingAtOnceShareOneRead()
    {
        await UploadAsync(Alice, "cube.gcode", Png(1));
        PrintJob job = Job(Alice, "cube.gcode", digest: null);
        PrintThumbnails thumbnails = Thumbnails;

        byte[]?[] images = await Task.WhenAll(Enumerable.Range(0, 16)
                                                        .Select(_ => Task.Run(() => thumbnails.ForAsync(job, TestContext.Current.CancellationToken))));

        images.Should().AllSatisfy(image => image.Should().BeSameAs(images[0]));
        images[0].Should().Equal(Png(1));
    }

    /// <summary>
    /// Only the last <see cref="PrintThumbnails.Capacity"/> prints are held, so a long-running
    /// deployment does not accumulate every preview it ever showed.
    /// </summary>
    [Fact]
    public async Task OnlyTheMostRecentPrintsAreHeld()
    {
        await UploadAsync(Alice, "cube.gcode", Png(1));
        PrintJob oldest = Job(Alice, "cube.gcode", digest: null);

        await Thumbnails.ForAsync(oldest, TestContext.Current.CancellationToken);

        for (int print = 0; print < PrintThumbnails.Capacity; print++)
        {
            await Thumbnails.ForAsync(Job(Alice, "cube.gcode", digest: null), TestContext.Current.CancellationToken);
        }

        await UploadAsync(Alice, "cube.gcode", Png(2), overwrite: true);

        (await Thumbnails.ForAsync(oldest, TestContext.Current.CancellationToken)).Should().Equal(Png(2));
    }

    private PrintThumbnails Thumbnails => _services.GetRequiredService<PrintThumbnails>();

    /// <summary>Stores a plain file carrying <paramref name="png"/> as its preview, and returns its digest.</summary>
    private async Task<string> UploadAsync(long userId, string name, byte[] png, bool overwrite = false)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();

        string encoded = Convert.ToBase64String(png);
        string gcode = $"; thumbnail begin 380x285 {encoded.Length}\n; {encoded}\n; thumbnail end\n\nG28\n";

        StoredFile file = await catalog.SaveAsync(Caller.Unscoped(userId), name,
                                                  new MemoryStream(Encoding.ASCII.GetBytes(gcode)), overwrite,
                                                  TestContext.Current.CancellationToken);

        PrintFile? row = await catalog.RowForAsync(userId, file, TestContext.Current.CancellationToken);

        return row!.Digest!;
    }

    private static PrintJob Job(long queuedBy, string fileName, string? digest)
    {
        return new PrintJob
        {
            PrintUuid = Guid.NewGuid(),
            PrinterId = 1,
            FileName = fileName,
            Digest = digest,
            QueuedByUserId = queuedBy,
            State = PrintState.Printing,
        };
    }

    private static byte[] Png(byte tag)
    {
        return [.. TinyPng, tag];
    }
}
