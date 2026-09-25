using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CameraAccessService.FindAsync"/> - and specifically the order its two refusals are
/// decided in, which is what keeps a UUID from being confirmed.
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching the other access-service
/// tests in this project.
/// </remarks>
public sealed class CameraAccessServiceTests : IDisposable
{
    private const long Alice = 1;

    private const long Mallory = 2;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-cameraaccess-{Guid.NewGuid():N}.db");

    /// <summary>
    /// <b>The case the ordering exists for.</b> A credential that never named <c>ViewCamera</c> is
    /// refused before the row is looked for, so a UUID naming nothing is refused exactly as a UUID
    /// naming somebody's camera is. Deciding it after the lookup answered <c>403</c> for a camera that
    /// exists - in any team - and <c>404</c> for one that does not, which confirms a guessed UUID.
    /// </summary>
    [Fact]
    public async Task AUuidNamingNoCameraIsRefusedByScopeJustAsARealOneIs()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context, Alice, "alice@example.com");
        Camera camera = await AddTeamWithCameraAsync(context, Alice, CapabilityPresets.Manager);

        CameraAccessService access = NewService(context);
        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

        // Act & Assert
        await FluentActions
              .Awaiting(() => access.FindAsync(Guid.NewGuid(), printing, Capability.ViewCamera,
                                               TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>("a UUID naming nothing must not be told apart from one that does");

        await FluentActions
              .Awaiting(() => access.FindAsync(camera.Uuid, printing, Capability.ViewCamera,
                                               TestContext.Current.CancellationToken))
              .Should()
              .ThrowAsync<CredentialScopeDeniedException>("and the camera this caller owns answers the same way");
    }

    /// <summary>
    /// <b>The team's refusal stays silent.</b> Moving the credential check first must not turn a
    /// membership refusal into one that names a capability: a caller whose credential narrows nothing
    /// and who is simply not on the camera's team still gets <see langword="null"/>, which is the
    /// answer a stranger's UUID and a missing one share.
    /// </summary>
    [Fact]
    public async Task ANonMemberWhoseCredentialNarrowsNothingStillGetsSilence()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context, Alice, "alice@example.com");
        await AddUserAsync(context, Mallory, "mallory@example.com");
        Camera camera = await AddTeamWithCameraAsync(context, Alice, CapabilityPresets.Manager);

        CameraAccessService access = NewService(context);

        // Act
        Camera? found = await access.FindAsync(camera.Uuid, Caller.Unscoped(Mallory), Capability.ViewCamera,
                                               TestContext.Current.CancellationToken);

        // Assert
        found.Should().BeNull("a camera belonging to somebody else is answered as if it were not there");
    }

    /// <summary>
    /// The member holding the capability still gets the camera - the reorder moved a refusal, not the
    /// answer.
    /// </summary>
    [Fact]
    public async Task AMemberHoldingTheCapabilityGetsTheCamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddUserAsync(context, Alice, "alice@example.com");
        Camera camera = await AddTeamWithCameraAsync(context, Alice, CapabilityPresets.Viewer);

        CameraAccessService access = NewService(context);

        // Act
        Camera? found = await access.FindAsync(camera.Uuid, Caller.Unscoped(Alice), Capability.ViewCamera,
                                               TestContext.Current.CancellationToken);

        // Assert
        found.Should().NotBeNull();
        found!.Uuid.Should().Be(camera.Uuid);
    }

    /// <summary>
    /// <b>A closed account's membership grants nothing here either.</b> The row stays, because history
    /// names the people in it, so the check has to ask for an open account rather than for the row -
    /// the same question every other membership decision asks.
    /// </summary>
    [Fact]
    public async Task AClosedAccountsMembershipFindsNoCamera()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser alice = await AddUserAsync(context, Alice, "alice@example.com");
        Camera camera = await AddTeamWithCameraAsync(context, Alice, CapabilityPresets.Manager);
        alice.DeactivatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CameraAccessService access = NewService(context);

        // Act
        Camera? found = await access.FindAsync(camera.Uuid, Caller.Unscoped(Alice), Capability.ViewCamera,
                                               TestContext.Current.CancellationToken);

        // Assert
        found.Should().BeNull("a closed account keeps its membership row and can do nothing with it");
    }

    /// <summary>
    /// Claiming a camera plugged into this machine is an administrator's act, and the question is the
    /// one every administrator decision asks: the role row <b>and</b> an open account. A closed
    /// administrator still holds the role row, so the service asks for an open account rather than
    /// resting on the session having ended with it.
    /// </summary>
    [Fact]
    public async Task AClosedAdministratorIsNotAnAdministrator()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser alice = await AddUserAsync(context, Alice, "alice@example.com");
        HSUser mallory = await AddUserAsync(context, Mallory, "mallory@example.com");
        IdentityRole<long> role = new(Accounts.AdminBootstrap.AdminRole) { NormalizedName = "ADMIN" };
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.UserRoles.Add(new IdentityUserRole<long> { UserId = alice.Id, RoleId = role.Id });
        context.UserRoles.Add(new IdentityUserRole<long> { UserId = mallory.Id, RoleId = role.Id });
        mallory.DeactivatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CameraAccessService access = NewService(context);

        // Act
        bool open = await access.IsAdministratorAsync(Alice, TestContext.Current.CancellationToken);
        bool closed = await access.IsAdministratorAsync(Mallory, TestContext.Current.CancellationToken);

        // Assert
        open.Should().BeTrue();
        closed.Should().BeFalse("the role row outlives the closure, and holding it is not administering");
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

    private static CameraAccessService NewService(HomespoolDbContext context)
    {
        return new CameraAccessService(context, new TeamCapabilityLookup(context));
    }

    private static async Task<HSUser> AddUserAsync(HomespoolDbContext context, long id, string email)
    {
        HSUser user = new(email)
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    /// <summary>A team the one member holds <paramref name="capabilities"/> on, and a camera in it.</summary>
    private static async Task<Camera> AddTeamWithCameraAsync(HomespoolDbContext context,
                                                             long userId,
                                                             IReadOnlyList<Capability> capabilities)
    {
        Team team = new()
        {
            Name = "workshop",
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Members =
            {
                new TeamMember
                {
                    UserId = userId,
                    Capabilities = CapabilitySet.Format(capabilities),
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "bed",
            Source = "rtsp://cam.lan/stream",
            TeamId = team.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Cameras.Add(camera);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera;
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
