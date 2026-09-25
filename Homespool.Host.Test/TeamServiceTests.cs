using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TeamService.GetAllTeamsAsync"/> - the admin lookup used to populate the team picker when
/// inviting someone into an existing team.
/// </summary>
public sealed class TeamServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-team-{Guid.NewGuid():N}.db");

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
        await using HomespoolDbContext context = await MigratedContextAsync();

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
        await using HomespoolDbContext context = await MigratedContextAsync();

        Team first = new() { Name = "First", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        Team second = new() { Name = "Second", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.AddRange(first, second);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await using HomespoolDbContext context = await MigratedContextAsync();

        Team owned = new() { Name = "Mine", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        Team someoneElses = new() { Name = "Not mine", CreatedBy = 2, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.AddRange(owned, someoneElses);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new TeamService(context).AddMemberAsync(owned.Id, 1, CapabilityPresets.Manager, CancellationToken.None);
        await new TeamService(context).AddMemberAsync(someoneElses.Id, 2, CapabilityPresets.Manager, CancellationToken.None);

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
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        IReadOnlyList<TeamMember> memberships = await new TeamService(context).GetTeamsForUserAsync(1, CancellationToken.None);

        // Assert
        memberships.Should().BeEmpty();
    }

    /// <summary>
    /// <b>A closed account's membership answers null from the three lookups a permission is decided
    /// on</b> - by team id, by team uuid, and the default team. The row stays, because history names
    /// the people in it, so what the lookups have to leave out is the closed account, not the row.
    /// </summary>
    [Fact]
    public async Task TheMembershipLookupsDoNotAnswerForAClosedAccount()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        HSUser open = await AddUserAsync(context, 1, "open@example.com");
        HSUser closed = await AddUserAsync(context, 2, "closed@example.com");
        closed.DeactivatedAt = DateTimeOffset.UtcNow;

        Team team = new() { Name = "Workshop", CreatedBy = open.Id, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (long userId in new[] { open.Id, closed.Id })
        {
            context.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id,
                UserId = userId,
                Capabilities = CapabilitySet.Format(CapabilityPresets.Manager),
                IsDefault = true,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TeamService teams = new(context);

        // Act & Assert
        (await teams.GetMemberAsync(team.Id, open.Id, CancellationToken.None)).Should().NotBeNull();
        (await teams.GetMemberAsync(team.Uuid, open.Id, CancellationToken.None)).Should().NotBeNull();
        (await teams.GetDefaultTeamMembershipAsync(open.Id, CancellationToken.None)).Should().NotBeNull();

        (await teams.GetMemberAsync(team.Id, closed.Id, CancellationToken.None)).Should().BeNull("by team id");
        (await teams.GetMemberAsync(team.Uuid, closed.Id, CancellationToken.None)).Should().BeNull("by team uuid");
        (await teams.GetDefaultTeamMembershipAsync(closed.Id, CancellationToken.None)).Should().BeNull("the default team");

        (await teams.GetMembersAsync(team.Id, CancellationToken.None))
            .Should().HaveCount(2, "the roster still lists the closed account");
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
}
