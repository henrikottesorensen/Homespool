using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What the startup reconciler will and will not hand back to the stream server, and which version of
/// a camera it hands over.
/// </summary>
/// <remarks>
/// <b>It puts stored sources back at every start, quietly, unattended, and with nobody watching.</b>
/// A row written before a rule existed, or by any future caller that goes round the service, would
/// otherwise be pushed to the sidecar unchecked; and a camera changed while it waits for the sidecar
/// would otherwise be pushed as it was before the change.
/// </remarks>
public sealed class CameraStreamReconcilerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"reconciler-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task AStoredAttachedSourceThisServerDidNotComposeIsNotRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid forged = await AddCameraAsync(
            context,
            "ffmpeg:device?video=/dev/v4l/by-id/usb-camera-video-index0#raw=-i#raw=/etc/hostname");

        using SidecarHandler handler = new();
        await RunReconcilerAsync(handler);

        handler.Registered.Should().NotContain(forged,
                                               "a source the server did not compose is a command line for the sidecar, " +
                                               "and re-registering it at every start would be the quietest way to keep it");
    }

    /// <summary>
    /// The guard must not swallow the cameras the reconciler exists to restore.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryNetworkCameraIsStillRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid network = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new();
        await RunReconcilerAsync(handler);

        handler.Registered.Should().Contain(network, "restoring these is the whole point of the reconciler");
    }

    /// <summary>
    /// A stored network source is checked again before it is handed over, against what its name
    /// resolves to now rather than at the save. Only a positive verdict - it points into this
    /// deployment - withholds it; a name that resolves to nothing is kept, since at start-up nobody
    /// can retry and such a name reaches nothing.
    /// </summary>
    [Fact]
    public async Task AStoredNetworkSourceThatNowPointsInsideTheDeploymentIsNotRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid rebound = await AddCameraAsync(context, "rtsp://camera.example/live");

        using SidecarHandler handler = new();
        await RunReconcilerAsync(handler,
                                 CameraSourcePolicyTests.Build(resolvesTo: "172.28.0.3", containerNetwork: "172.28.0.0/16"));

        handler.Registered.Should().NotContain(rebound,
                                               "a name that has come to point inside the deployment is exactly what " +
                                               "re-checking at start-up exists to catch");
    }

    [Fact]
    public async Task AStoredNetworkSourceWhoseNameDoesNotResolveIsStillRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid unresolved = await AddCameraAsync(context, "rtsp://camera.example/live");

        using SidecarHandler handler = new();
        await RunReconcilerAsync(handler, CameraSourcePolicyTests.Build(unresolvable: true));

        handler.Registered.Should().Contain(unresolved,
                                            "DNS not answering at boot must not cost a camera its registration");
    }

    /// <summary>
    /// The configurer can restart the sidecar just before the reconciler runs, and a restarting
    /// sidecar refuses connections for a moment. That is a listing to ask for again, not a reason to
    /// leave every camera to its next save.
    /// </summary>
    [Fact]
    public async Task AListingRefusedWhileTheSidecarRestartsIsAskedForAgain()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new() { ListingsToRefuse = 2 };
        await RunReconcilerAsync(handler);

        handler.Listings.Should().Be(4, "three until the sidecar answered, then the sync asking whether it holds the camera");
        handler.Registered.Should().Contain(camera, "the sidecar answered once it was back");
    }

    /// <summary>
    /// A sidecar that never answers is given up on, so the reconciler does not sit in a loop for the
    /// life of the process.
    /// </summary>
    [Fact]
    public async Task ASidecarThatNeverAnswersIsGivenUpOn()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        _ = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new() { ListingsToRefuse = int.MaxValue };
        await RunReconcilerAsync(handler);

        handler.Listings.Should().BeInRange(2, 31, "asked again once a second for thirty seconds, not in a busy loop");
        handler.Attempted.Should().BeEmpty("with no listing, nothing is known to be missing");
    }

    /// <summary>
    /// A stream no camera owns is swept once a restarting sidecar is back - with no cameras at all,
    /// which is when the last camera's removal left it. Swept from the listing the wait produced, not
    /// from one asked for before it, which a restarting sidecar would have refused.
    /// </summary>
    [Fact]
    public async Task AnOrphanIsSweptOnceARestartingSidecarIsBack()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid orphan = Guid.NewGuid();

        using SidecarHandler handler = new(Held(orphan, "rtsp://192.0.2.99/orphan")) { ListingsToRefuse = 2 };
        await RunReconcilerAsync(handler);

        handler.Deleted.Should().Equal([orphan.ToString("D")], "the sidecar answered once it was back, and the stream is no camera's");
        handler.Listings.Should().Be(3, "the sweep uses the listing the wait produced rather than asking again");
    }

    /// <summary>
    /// For a moment after it is back, go2rtc lists its streams but refuses every source, in the words
    /// it refuses a bad one with: its stream list comes up before the modules that register the source
    /// schemes. The retry is what lets such a camera through.
    /// </summary>
    [Fact]
    public async Task ASourceRefusedJustAfterARestartIsTriedAgain()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new() { RegistrationsToRefuse = 1 };
        await RunReconcilerAsync(handler);

        handler.Attempted.Should().Equal(camera, camera);
        handler.Registered.Should().Contain(camera);
        (handler.AttemptedAt[1] - handler.AttemptedAt[0]).Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(5), "an immediate retry would land in the same moment the sidecar refused the first");
    }

    /// <summary>
    /// A registration the sidecar did not answer at all is tried again too, for the same restart.
    /// </summary>
    [Fact]
    public async Task ARegistrationThatWentUnansweredIsTriedAgain()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new() { RegistrationsToDrop = 1 };
        await RunReconcilerAsync(handler);

        handler.Attempted.Should().Equal(camera, camera);
        handler.Registered.Should().Contain(camera);
    }

    /// <summary>
    /// A source the sidecar really does refuse is retried once and then left alone.
    /// </summary>
    [Fact]
    public async Task ASourceThatIsAlwaysRefusedIsTriedTwiceAndNoMore()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new() { RegistrationsToRefuse = int.MaxValue };
        await RunReconcilerAsync(handler);

        handler.Attempted.Should().Equal(camera, camera);
        handler.Registered.Should().BeEmpty();
    }

    /// <summary>
    /// A camera edited while the reconciler waits for a restarting sidecar reaches it as edited, not as
    /// it was when the reconciler started - which the sidecar's file would otherwise keep for good.
    /// </summary>
    [Fact]
    public async Task ACameraEditedWhileTheSidecarRestartsIsRegisteredAsEdited()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new()
        {
            ListingsToRefuse = 2,
            OnListing = async before =>
            {
                if (before == 0)
                {
                    await SetSourceAsync(camera, "rtsp://192.0.2.20/edited");
                }
            },
        };

        await RunReconcilerAsync(handler);

        handler.AttemptedSources.Should().Equal(["rtsp://192.0.2.20/edited"], "the camera as it is, not as it was");
    }

    /// <summary>
    /// A camera removed while the reconciler waits is not put back - and the stream it had is removed.
    /// </summary>
    [Fact]
    public async Task ACameraRemovedWhileTheSidecarRestartsIsNotPutBack()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new(Held(camera, "rtsp://192.0.2.10/live"))
        {
            ListingsToRefuse = 2,
            OnListing = async before =>
            {
                if (before == 0)
                {
                    await RemoveCameraAsync(camera);
                }
            },
        };

        await RunReconcilerAsync(handler);

        handler.Attempted.Should().BeEmpty("there is no camera left to register");
        handler.Streams.Should().NotContainKey(camera.ToString("D"), "a stream no camera owns is removed");
    }

    /// <summary>
    /// A camera edited between a refused registration and its retry is retried as edited.
    /// </summary>
    [Fact]
    public async Task ACameraEditedBeforeItsRetryIsRetriedAsEdited()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new()
        {
            RegistrationsToRefuse = 1,
            OnRegistration = async (before, _) =>
            {
                if (before == 0)
                {
                    await SetSourceAsync(camera, "rtsp://192.0.2.20/edited");
                }
            },
        };

        await RunReconcilerAsync(handler);

        handler.AttemptedSources.Should().Equal("rtsp://192.0.2.10/live", "rtsp://192.0.2.20/edited");
        handler.Streams[camera.ToString("D")].Should().Be("rtsp://192.0.2.20/edited");
    }

    /// <summary>
    /// An attached camera missing when Homespool starts - unplugged, not yet enumerated, its mount not
    /// there yet - keeps the stream go2rtc holds for it. The rig's device list is empty, so the camera
    /// here is missing.
    /// </summary>
    [Fact]
    public async Task AnAttachedCameraMissingAtStartKeepsItsStream()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        string source = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0");
        Guid camera = await AddCameraAsync(context, source);

        using SidecarHandler handler = new(Held(camera, source));
        await RunReconcilerAsync(handler);

        handler.Deleted.Should().BeEmpty();
        handler.Streams[camera.ToString("D")].Should().Be(source);
    }

    /// <summary>
    /// A stream already holding exactly the camera's source is left alone: replacing it would hand a
    /// viewer who is already watching a second reader on the camera.
    /// </summary>
    [Fact]
    public async Task AStreamAlreadyHoldingTheCamerasSourceIsLeftAlone()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new(Held(camera, "rtsp://192.0.2.10/live"));
        await RunReconcilerAsync(handler);

        handler.Attempted.Should().BeEmpty();
    }

    /// <summary>
    /// A stream the sidecar holds with a source the camera no longer has is replaced: every camera is
    /// checked at start, not only the ones the sidecar is missing.
    /// </summary>
    [Fact]
    public async Task AStreamHoldingAnOlderSourceIsReplaced()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid camera = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using SidecarHandler handler = new(Held(camera, "rtsp://192.0.2.10/older"));
        await RunReconcilerAsync(handler);

        handler.AttemptedSources.Should().Equal("rtsp://192.0.2.10/live");
    }

    /// <summary>
    /// A stream the sidecar already holds, for a source a rule now refuses, is removed. Checking only
    /// the cameras the sidecar was missing left such a stream in place for good.
    /// </summary>
    [Fact]
    public async Task AHeldStreamWhoseSourceIsNowRefusedIsRemoved()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        const string Forged = "ffmpeg:device?video=/dev/v4l/by-id/usb-camera-video-index0#raw=-i#raw=/etc/hostname";
        Guid camera = await AddCameraAsync(context, Forged);

        using SidecarHandler handler = new(Held(camera, Forged));
        await RunReconcilerAsync(handler);

        handler.Attempted.Should().BeEmpty();
        handler.Streams.Should().NotContainKey(camera.ToString("D"));
    }

    private async Task RunReconcilerAsync(SidecarHandler handler, CameraSourcePolicy? policy = null)
    {
        using StreamSyncRig rig = new(_databasePath, handler, policy);

        FakeTimeProvider time = new();
        handler.Clock = time;

        using CameraStreamReconciler reconciler = new(
            rig.Scopes,
            rig.Client,
            rig.Sync,
            new CameraLiveAvailability(Substitute.For<ICameraCodecProbe>()),
            time,
            NullLogger<CameraStreamReconciler>.Instance);

        await reconciler.StartAsync(TestContext.Current.CancellationToken);

        // The clock is moved on until the sweep finishes, a quarter-second at a time so no wait is
        // stepped over by much. The bound is only there so a reconciler that never stops fails
        // rather than hangs: ten minutes is far past every wait it has.
        for (int step = 0; step < 2400 && !reconciler.ExecuteTask!.IsCompleted; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        reconciler.ExecuteTask!.IsCompleted.Should().BeTrue("the reconciler runs once and stops");
        await reconciler.ExecuteTask;
        await reconciler.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Held(Guid stream, string source)
    {
        return new Dictionary<string, string> { [stream.ToString("D")] = source };
    }

    private static async Task<Guid> AddCameraAsync(HomespoolDbContext context, string source)
    {
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

    /// <summary>Changes a camera's source the way a save does, from a context of its own.</summary>
    private async Task SetSourceAsync(Guid camera, string source)
    {
        await using HomespoolDbContext context = NewContext();

        await context.Cameras
                     .Where(row => row.Uuid == camera)
                     .ExecuteUpdateAsync(row => row.SetProperty(c => c.Source, source), TestContext.Current.CancellationToken);
    }

    /// <summary>Removes a camera's row the way a removal does, from a context of its own.</summary>
    private async Task RemoveCameraAsync(Guid camera)
    {
        await using HomespoolDbContext context = NewContext();

        await context.Cameras
                     .Where(row => row.Uuid == camera)
                     .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
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

    public void Dispose()
    {
        TestSqlitePool.Release(_databasePath);

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
