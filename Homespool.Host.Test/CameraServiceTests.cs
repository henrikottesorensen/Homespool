using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CameraService"/>'s credential check - the create path, which is the one that resolves a
/// team and reads a membership row itself rather than going through
/// <see cref="CameraAccessService.FindAsync"/> - and each save made by a key holding no more than it
/// needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refusals run against a service holding only what a refused create touches</b>, so reaching
/// anything further fails on a null. <b>Saves run against <see cref="StreamSyncRig"/></b>, whose
/// sidecar accepts every registration: what is asserted is that the key got through and the row was
/// written, not what the stream server or the picture did, which have suites of their own.
/// </para>
/// <para>
/// Run against real SQLite rather than the in-memory provider, matching the other service tests in
/// this project.
/// </para>
/// </remarks>
public sealed class CameraServiceTests : IDisposable
{
    private const long Alice = 1;

    /// <summary>A network camera the source policy accepts: it resolves to a public address.</summary>
    private const string NetworkSource = "rtsp://camera.example/live";

    private static readonly IOptionsMonitor<CameraOptions> Options = TestOptions.Monitor(new CameraOptions
    {
        ApiUsername = "homespool",
        ApiPassword = "secret", // betterleaks:allow - the sidecar is a handler in this test
    });

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-cameraservice-{Guid.NewGuid():N}.db");

    /// <summary>
    /// <b>The team uuid must not be told apart either.</b> A team this account is not in is refused
    /// with the answer a team that does not exist gets, so a credential that never named
    /// <c>ManageCamera</c> has to be refused before the uuid is resolved - otherwise the outcome for
    /// a stranger's team and the throw for a real one say which is which.
    /// </summary>
    [Fact]
    public async Task ACredentialWithoutManageCameraIsRefusedBeforeTheTeamUuidIsResolved()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);

        CameraService cameras = NewService(context);
        Caller printing = TestCallers.Scoped(Alice, Capability.Print);

        // Act & Assert
        await FluentActions
              .Awaiting(() => cameras.CreateAsync(printing, Guid.NewGuid(), "spare", "rtsp://cam.lan/stream",
                                                  printerUuid: null, resolution: null,
                                                  TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>("a team uuid naming nothing must not be told apart");

        await FluentActions
              .Awaiting(() => cameras.CreateAsync(printing, team.Uuid, "spare", "rtsp://cam.lan/stream",
                                                  printerUuid: null, resolution: null,
                                                  TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>("and this account's own team answers the same way");
    }

    /// <summary>
    /// <b>An attached camera is no exception.</b> The attached-device branch answers on the
    /// administrator role alone, so a credential that never named <c>ManageCamera</c> would otherwise
    /// claim a device on the strength of its owner's role.
    /// </summary>
    [Fact]
    public async Task ACredentialWithoutManageCameraCannotClaimAnAttachedDevice()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);

        CameraService cameras = NewService(context);
        Caller printing = TestCallers.Scoped(Alice, Capability.Print);

        // Act & Assert
        await FluentActions
              .Awaiting(() => cameras.CreateAsync(printing, team.Uuid, "bed",
                                                  "ffmpeg:device?video=/dev/v4l/by-id/usb-Acme_Cam",
                                                  printerUuid: null, resolution: "1280x720",
                                                  TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>();
    }

    /// <summary>
    /// <b>A closed account's membership cannot save a camera.</b> The create path reads the membership
    /// itself, so it has to ask for an open account on its own account - the row outlives the closure.
    /// </summary>
    [Fact]
    public async Task AClosedAccountsMembershipCannotSaveACamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser alice = await AddUserAsync(context);
        Team team = await AddTeamAsync(context);
        alice.DeactivatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CameraService cameras = NewService(context);

        // Act
        CameraSaveOutcome outcome = await cameras.CreateAsync(Caller.Unscoped(Alice), team.Uuid, "bed",
                                                              "rtsp://cam.lan/stream", printerUuid: null,
                                                              resolution: null, TestContext.Current.CancellationToken);

        // Assert
        outcome.Saved.Should().BeFalse();
        outcome.Error!.Key.Should().Be("Cameras_NotYourTeam", "the answer a team the caller is not in gets");
    }

    /// <summary>Adding a camera is <c>ManageCamera</c>, and a key holding that alone adds one.</summary>
    [Fact]
    public async Task AKeyHoldingManageCameraAddsACamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);
        using SidecarHandler sidecar = new();
        using StreamSyncRig rig = new(_databasePath, sidecar);
        using IServiceScope scope = rig.Scopes.CreateScope();

        // Act
        CameraSaveOutcome outcome = await SavingService(context, rig, scope)
                                          .CreateAsync(TestCallers.Scoped(Alice, Capability.ManageCamera), team.Uuid, "Bench",
                                                       NetworkSource, printerUuid: null, resolution: null, CancellationToken.None);

        // Assert
        outcome.Saved.Should().BeTrue();
        (await context.Cameras.SingleAsync(TestContext.Current.CancellationToken)).TeamId.Should().Be(team.Id);
    }

    /// <summary>Changing a camera is <c>ManageCamera</c> too, with nothing else beside it.</summary>
    [Fact]
    public async Task AKeyHoldingManageCameraChangesACamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);
        using SidecarHandler sidecar = new();
        using StreamSyncRig rig = new(_databasePath, sidecar);
        using IServiceScope scope = rig.Scopes.CreateScope();
        CameraService service = SavingService(context, rig, scope);

        Camera camera = (await service.CreateAsync(Caller.Unscoped(Alice), team.Uuid, "Bench", NetworkSource,
                                                   printerUuid: null, resolution: null, CancellationToken.None)).Camera!;

        // Act
        CameraSaveOutcome outcome = await service.UpdateAsync(TestCallers.Scoped(Alice, Capability.ManageCamera), camera.Uuid, "Door",
                                                              NetworkSource, printerUuid: null, resolution: null, CancellationToken.None);

        // Assert
        outcome.Saved.Should().BeTrue();
        (await context.Cameras.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Name.Should().Be("Door");
    }

    /// <summary>Removing a camera is <c>ManageCamera</c>, and the row goes.</summary>
    [Fact]
    public async Task AKeyHoldingManageCameraRemovesACamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);
        using SidecarHandler sidecar = new();
        using StreamSyncRig rig = new(_databasePath, sidecar);
        using IServiceScope scope = rig.Scopes.CreateScope();
        CameraService service = SavingService(context, rig, scope);

        Camera camera = (await service.CreateAsync(Caller.Unscoped(Alice), team.Uuid, "Bench", NetworkSource,
                                                   printerUuid: null, resolution: null, CancellationToken.None)).Camera!;

        // Act
        bool removed = await service.DeleteAsync(TestCallers.Scoped(Alice, Capability.ManageCamera), camera.Uuid, CancellationToken.None);

        // Assert
        removed.Should().BeTrue();
        (await context.Cameras.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// <b>Attaching a camera to a printer also takes <c>ViewPrinter</c> on that printer</b> - a printer
    /// you cannot see is one you cannot name. A key holding <c>ManageCamera</c> alone is refused naming
    /// it, and saves nothing; with both it attaches.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachingACameraToAPrinterAlsoTakesViewPrinter(bool canSeePrinters)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context);
        Team team = await AddTeamAsync(context);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = team.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using SidecarHandler sidecar = new();
        using StreamSyncRig rig = new(_databasePath, sidecar);
        using IServiceScope scope = rig.Scopes.CreateScope();
        Caller key = canSeePrinters ?
            TestCallers.Scoped(Alice, Capability.ManageCamera, Capability.ViewPrinter) :
            TestCallers.Scoped(Alice, Capability.ManageCamera);

        // Act
        Func<Task<CameraSaveOutcome>> create = () => SavingService(context, rig, scope)
                                                     .CreateAsync(key, team.Uuid, "Bench", NetworkSource, printer.Uuid,
                                                                  resolution: null, CancellationToken.None);

        // Assert
        if (canSeePrinters)
        {
            (await create()).Saved.Should().BeTrue();
            (await context.Cameras.SingleAsync(TestContext.Current.CancellationToken)).PrinterId.Should().Be(printer.Id);
        }
        else
        {
            (await create.Should().ThrowAsync<CredentialScopeDeniedException>()).Which.Missing.Should().Be(Capability.ViewPrinter);
            (await context.Cameras.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        }
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The service with only what a refused create touches. The rest is null on purpose: reaching any
    /// of it would be the failure these cases exist to catch, and a null says so louder than a
    /// substitute that quietly answers.
    /// </summary>
    private static CameraService NewService(HomespoolDbContext context)
    {
        return new CameraService(context,
                                 new CameraAccessService(context, new TeamCapabilityLookup(context)),
                                 printerAccess: null!,
                                 sourcePolicy: null!,
                                 streamServer: null!,
                                 sweeper: null!,
                                 sync: null!,
                                 fetcher: null!,
                                 frames: null!,
                                 liveView: null!,
                                 devices: null!,
                                 credentials: null!,
                                 timeProvider: null!,
                                 options: null!);
    }

    /// <summary>
    /// The service as the application builds it, its stream server being the rig's sidecar. The
    /// protector, policy and devices are the rig's own, so what this service writes is what the rig's
    /// sync reads back.
    /// </summary>
    private static CameraService SavingService(HomespoolDbContext context, StreamSyncRig rig, IServiceScope scope)
    {
        IServiceProvider services = scope.ServiceProvider;
        ICameraSnapshotFetcher fetcher = Substitute.For<ICameraSnapshotFetcher>();

        return new CameraService(context,
                                 new CameraAccessService(context, new TeamCapabilityLookup(context)),
                                 new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                 services.GetRequiredService<CameraSourcePolicy>(),
                                 rig.Client,
                                 services.GetRequiredService<CameraStreamSweeper>(),
                                 rig.Sync,
                                 fetcher,
                                 new CameraFrameCache(fetcher, Options, TimeProvider.System, NullLogger<CameraFrameCache>.Instance),
                                 new CameraLiveAvailability(Substitute.For<ICameraCodecProbe>()),
                                 services.GetRequiredService<LocalCameraDevices>(),
                                 services.GetRequiredService<CameraCredentialProtector>(),
                                 TimeProvider.System,
                                 Options);
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

    private static async Task<Team> AddTeamAsync(HomespoolDbContext context)
    {
        Team team = new()
        {
            Name = "workshop",
            CreatedBy = Alice,
            CreatedAt = DateTimeOffset.UtcNow,
            Members =
            {
                new TeamMember
                {
                    UserId = Alice,
                    Capabilities = CapabilitySet.Format(CapabilityPresets.Manager),
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team;
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
