using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CameraStreamSync"/>: the sidecar ends holding what the camera's row says, whoever asked
/// and in whatever order.
/// </summary>
public sealed class CameraStreamSyncTests : IDisposable
{
    private const string Source = "rtsp://192.0.2.10/live";
    private const string Edited = "rtsp://192.0.2.20/edited";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"stream-sync-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task ACameraTheSidecarDoesNotHoldIsRegistered()
    {
        Guid camera = await AddCameraAsync(Source);

        using SidecarHandler handler = new();
        using StreamSyncRig rig = new(_databasePath, handler);

        StreamSync sync = await rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Registered);
        handler.Streams[Name(camera)].Should().Be(Source);
    }

    [Fact]
    public async Task ACameraTheSidecarAlreadyHoldsIsLeftAlone()
    {
        Guid camera = await AddCameraAsync(Source);

        using SidecarHandler handler = new(Held(camera, Source));
        using StreamSyncRig rig = new(_databasePath, handler);

        StreamSync sync = await rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Unchanged);
        handler.Attempted.Should().BeEmpty("a replacement would give a viewer already watching a second reader on the camera");
    }

    /// <summary>
    /// A stream still in the file but no longer running - a removal whose file write failed - is not
    /// held, whatever the file says.
    /// </summary>
    [Fact]
    public async Task AStreamOnlyInTheFileIsRegisteredAgain()
    {
        Guid camera = await AddCameraAsync(Source);

        using SidecarHandler handler = new(Held(camera, Source));
        handler.NotRunning.Add(Name(camera));
        using StreamSyncRig rig = new(_databasePath, handler);

        StreamSync sync = await rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Registered);
        handler.AttemptedSources.Should().Equal(Source);
    }

    [Fact]
    public async Task ACameraWithNoRowHasItsStreamRemoved()
    {
        Guid gone = Guid.NewGuid();

        using SidecarHandler handler = new(Held(gone, Source));
        using StreamSyncRig rig = new(_databasePath, handler);
        await MigrateAsync();

        StreamSync sync = await rig.Sync.SyncAsync(gone, TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Removed);
        handler.Streams.Should().NotContainKey(Name(gone));
    }

    /// <summary>
    /// A source a rule refuses is not handed over, the stream the sidecar held for it is removed, and
    /// the rule's own words come back for the page to show.
    /// </summary>
    [Fact]
    public async Task ARefusedSourceIsWithheldAndItsStreamRemoved()
    {
        const string Forged = "ffmpeg:device?video=/dev/v4l/by-id/usb-camera-video-index0#raw=-i#raw=/etc/hostname";
        Guid camera = await AddCameraAsync(Forged);

        using SidecarHandler handler = new(Held(camera, Forged));
        using StreamSyncRig rig = new(_databasePath, handler);

        StreamSync sync = await rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Withheld);
        sync.Refusal.Should().NotBeNull();
        handler.Attempted.Should().BeEmpty();
        handler.Streams.Should().NotContainKey(Name(camera));
    }

    /// <summary>
    /// Two syncs of one camera take turns, and the second reads the row when its turn comes - so a
    /// save committed while an earlier registration is still in flight is the one the sidecar keeps.
    /// Without the turn, the two arrive in either order, and the older source can land last.
    /// </summary>
    [Fact]
    public async Task ASecondSyncOfOneCameraWaitsAndRegistersTheRowAsItIsThen()
    {
        Guid camera = await AddCameraAsync(Source);

        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using SidecarHandler handler = new()
        {
            OnRegistration = async (before, _) =>
            {
                if (before == 0)
                {
                    firstArrived.SetResult();
                    await releaseFirst.Task;
                }
            },
        };

        using StreamSyncRig rig = new(_databasePath, handler);

        Task<StreamSync> first = rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);
        await firstArrived.Task.WaitAsync(TestContext.Current.CancellationToken);

        await SetSourceAsync(camera, Edited);
        Task<StreamSync> second = rig.Sync.SyncAsync(camera, TestContext.Current.CancellationToken);

        // Long enough for a second sync that did not wait to reach the sidecar.
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        handler.Attempted.Should().HaveCount(1, "the second sync waits for the first to finish");

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        handler.AttemptedSources.Should().Equal(Source, Edited);
        handler.Streams[Name(camera)].Should().Be(Edited, "the sidecar ends holding what the row says");
    }

    /// <summary>
    /// The turn is per camera: one camera's slow registration does not hold up another's.
    /// </summary>
    [Fact]
    public async Task AnotherCamerasSyncDoesNotWait()
    {
        Guid slow = await AddCameraAsync(Source);
        Guid other = await AddCameraAsync(Edited);

        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using SidecarHandler handler = new()
        {
            OnRegistration = async (_, source) =>
            {
                if (source == Source)
                {
                    await releaseSlow.Task;
                }
            },
        };

        using StreamSyncRig rig = new(_databasePath, handler);

        Task<StreamSync> blocked = rig.Sync.SyncAsync(slow, TestContext.Current.CancellationToken);

        StreamSync sync = await rig.Sync.SyncAsync(other, TestContext.Current.CancellationToken)
                                   .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        sync.Outcome.Should().Be(StreamSyncOutcome.Registered);
        blocked.IsCompleted.Should().BeFalse();

        releaseSlow.SetResult();
        await blocked;
    }

    public void Dispose()
    {
        TestSqlitePool.Release(_databasePath);

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private static string Name(Guid camera)
    {
        return camera.ToString("D");
    }

    private static Dictionary<string, string> Held(Guid stream, string source)
    {
        return new Dictionary<string, string> { [Name(stream)] = source };
    }

    private async Task<Guid> AddCameraAsync(string source)
    {
        await MigrateAsync();
        await using HomespoolDbContext context = NewContext();

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UnixEpoch };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "camera",
            Source = source,
            TeamId = team.Id,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        context.Cameras.Add(camera);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }

    private async Task SetSourceAsync(Guid camera, string source)
    {
        await using HomespoolDbContext context = NewContext();

        await context.Cameras
                     .Where(row => row.Uuid == camera)
                     .ExecuteUpdateAsync(row => row.SetProperty(c => c.Source, source), TestContext.Current.CancellationToken);
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    /// <summary>Migrates the test's database, which is harmless to repeat.</summary>
    private async Task MigrateAsync()
    {
        await using HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }
}
