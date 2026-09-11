using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What the startup reconciler will and will not hand back to the stream server.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one path that registers a stored source without it passing through
/// <c>CameraService</c>.</b> A row written before the composed-source rule existed, or by any future
/// caller that goes round the service, would otherwise be pushed to the sidecar unchecked at every
/// start - quietly, unattended, and with nobody watching, which is exactly the shape of thing this
/// reconciler is for.
/// </para>
/// <para>
/// The device list is empty here, because <c>LocalCameraDevices</c> reads a directory that exists
/// only in the container. So every attached source is one naming a device this machine does not
/// have, which is the same verdict a forged name earns on a real machine.
/// </para>
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

        using RecordingHandler handler = new();
        await RunReconcilerAsync(context, handler);

        handler.Registered.Should().NotContain(forged,
                                               "a source the server did not compose is a command line for the sidecar, "
                                               + "and re-registering it at every start would be the quietest way to keep it");
    }

    /// <summary>
    /// The guard must not swallow the cameras the reconciler exists to restore.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryNetworkCameraIsStillRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid network = await AddCameraAsync(context, "rtsp://192.0.2.10/live");

        using RecordingHandler handler = new();
        await RunReconcilerAsync(context, handler);

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

        using RecordingHandler handler = new();
        await RunReconcilerAsync(context,
                                 handler,
                                 CameraSourcePolicyTests.Build(resolvesTo: "172.28.0.3", containerNetwork: "172.28.0.0/16"));

        handler.Registered.Should().NotContain(rebound,
                                               "a name that has come to point inside the deployment is exactly what "
                                               + "re-checking at start-up exists to catch");
    }

    [Fact]
    public async Task AStoredNetworkSourceWhoseNameDoesNotResolveIsStillRegistered()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Guid unresolved = await AddCameraAsync(context, "rtsp://camera.example/live");

        using RecordingHandler handler = new();
        await RunReconcilerAsync(context, handler, CameraSourcePolicyTests.Build(unresolvable: true));

        handler.Registered.Should().Contain(unresolved,
                                            "DNS not answering at boot must not cost a camera its registration");
    }

    private static async Task RunReconcilerAsync(HomespoolDbContext context,
                                                 RecordingHandler handler,
                                                 CameraSourcePolicy? policy = null)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        Go2RtcClient streamServer = new(factory,
                                        TestOptions.Monitor(new CameraOptions
                                        {
                                            ApiUsername = "homespool",
                                            ApiPassword = "secret", // betterleaks:allow - the sidecar is a handler in this file
                                        }),
                                        NullLogger<Go2RtcClient>.Instance);

        IServiceScopeFactory scopes = ScopeFactoryFor(context);

        using CameraStreamReconciler reconciler = new(
            scopes,
            streamServer,
            new CameraLiveAvailability(Substitute.For<ICameraCodecProbe>()),
            new CameraCredentialProtector(new EphemeralDataProtectionProvider(),
                                          NullLogger<CameraCredentialProtector>.Instance),
            new LocalCameraDevices(NullLogger<LocalCameraDevices>.Instance,
                                   new UsbDeviceNames(NullLogger<UsbDeviceNames>.Instance)),
            policy ?? CameraSourcePolicyTests.Build(),
            NullLogger<CameraStreamReconciler>.Instance);

        await reconciler.StartAsync(TestContext.Current.CancellationToken);
        await reconciler.ExecuteTask!;
        await reconciler.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A scope factory over one already-migrated context, so the reconciler resolves the same
    /// database this test seeded.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(HomespoolDbContext context)
    {
        ServiceCollection services = [];
        services.AddScoped(_ => context);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
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

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    /// <summary>
    /// Answers the sidecar's calls and remembers which stream names were PUT.
    /// </summary>
    /// <remarks>
    /// The listing answers empty, so every camera counts as missing and the registration loop is
    /// actually entered - which is the loop under test. Anything else answers 200 with an empty
    /// object, since the probe that follows is not what this is about.
    /// </remarks>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Guid> Registered { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && request.RequestUri!.AbsolutePath == "/api/streams"
                && System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["name"] is { } name
                && Guid.TryParse(name, out Guid uuid))
            {
                Registered.Add(uuid);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
