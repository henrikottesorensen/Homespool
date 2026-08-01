using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TeamService.GetAllTeamsAsync"/> - the admin lookup used to populate the team picker when
/// inviting someone into an existing team (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
public sealed class TeamServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-team-{Guid.NewGuid():N}.db");

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
        await context.Database.MigrateAsync();

        return context;
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

    /// <summary>No teams yet yields an empty list, not null or an error.</summary>
    [Fact]
    public async Task GetAllTeamsAsyncReturnsAnEmptyListWhenNoTeamsExist()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        // Act
        IReadOnlyList<Team> teams = await new TeamService(context).GetAllTeamsAsync(CancellationToken.None);

        // Assert
        teams.Should().BeEmpty();
    }

    /// <summary>Teams come back oldest (lowest id) first, matching creation order.</summary>
    [Fact]
    public async Task GetAllTeamsAsyncReturnsTeamsOldestFirst()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        Team first = new() { Name = "First", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        Team second = new() { Name = "Second", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.AddRange(first, second);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<Team> teams = await new TeamService(context).GetAllTeamsAsync(CancellationToken.None);

        // Assert
        teams.Select(t => t.Id).Should().ContainInOrder(first.Id, second.Id);
    }

    // ---------- GetTeamsForUserAsync ----------

    /// <summary>
    /// Only the caller's own memberships come back, each with its <see cref="Team"/> loaded - the
    /// shape <c>GET /api/v1/user</c>'s <c>teams[]</c> needs.
    /// </summary>
    [Fact]
    public async Task GetTeamsForUserAsyncReturnsOnlyTheCallersMembershipsWithTeamLoaded()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        Team owned = new() { Name = "Mine", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        Team someoneElses = new() { Name = "Not mine", CreatedBy = 2, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.AddRange(owned, someoneElses);
        await context.SaveChangesAsync();

        await new TeamService(context).AddMemberAsync(owned.Id, 1, canRead: true, canUse: true, canManage: true, CancellationToken.None);
        await new TeamService(context).AddMemberAsync(someoneElses.Id, 2, canRead: true, canUse: true, canManage: true, CancellationToken.None);

        // Act
        IReadOnlyList<TeamMember> memberships = await new TeamService(context).GetTeamsForUserAsync(1, CancellationToken.None);

        // Assert
        memberships.Should().ContainSingle();
        memberships[0].TeamId.Should().Be(owned.Id);
        memberships[0].Team.Should().NotBeNull();
        memberships[0].Team!.Name.Should().Be("Mine");
    }

    /// <summary>No memberships yields an empty list, not null or an error.</summary>
    [Fact]
    public async Task GetTeamsForUserAsyncReturnsAnEmptyListWhenTheUserHasNoMemberships()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        // Act
        IReadOnlyList<TeamMember> memberships = await new TeamService(context).GetTeamsForUserAsync(1, CancellationToken.None);

        // Assert
        memberships.Should().BeEmpty();
    }
}
