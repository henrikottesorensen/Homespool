using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using PrinterService.Data;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Api.Test;

/// <summary>
/// <see cref="TeamService.GetAllTeamsAsync"/> - the admin lookup used to populate the team picker when
/// inviting someone into an existing team (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
public sealed class TeamServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-team-{Guid.NewGuid():N}.db");

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
    }

    private async Task<PSDbContext> MigratedContextAsync()
    {
        PSDbContext context = NewContext();
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
        await using PSDbContext context = await MigratedContextAsync();

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
        await using PSDbContext context = await MigratedContextAsync();

        Team first = new() { Name = "First", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        Team second = new() { Name = "Second", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.AddRange(first, second);
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<Team> teams = await new TeamService(context).GetAllTeamsAsync(CancellationToken.None);

        // Assert
        teams.Select(t => t.Id).Should().ContainInOrder(first.Id, second.Id);
    }
}
