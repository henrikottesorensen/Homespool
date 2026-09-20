using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

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
/// <see cref="CameraAccessService.FindAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the refusal is covered here. Saving a camera needs the stream server, the source policy and
/// the credential protector, and those have suites of their own; a caller refused by its scope
/// reaches none of them, which is the property being asserted.
/// </para>
/// <para>
/// Run against real SQLite rather than the in-memory provider, matching the other service tests in
/// this project.
/// </para>
/// </remarks>
public sealed class CameraServiceTests : IDisposable
{
    private const long Alice = 1;

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
        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

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
        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

        // Act & Assert
        await FluentActions
              .Awaiting(() => cameras.CreateAsync(printing, team.Uuid, "bed",
                                                  "ffmpeg:device?video=/dev/v4l/by-id/usb-Acme_Cam",
                                                  printerUuid: null, resolution: "1280x720",
                                                  TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>();
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
                                 fetcher: null!,
                                 frames: null!,
                                 liveView: null!,
                                 devices: null!,
                                 credentials: null!,
                                 timeProvider: null!,
                                 options: null!);
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
