using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Pages.Admin.Invites;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

/// <summary>
/// The admin invitation list: status/target display and the revoke action
/// (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
public sealed class IndexModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-invite-index-{Guid.NewGuid():N}.db");

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

    private static InvitationService NewInvitationService(PSDbContext context) =>
        new(context, new TokenService(), Options.Create(new InvitationOptions()));

    private static IndexModel NewModel(PSDbContext context) =>
        new(NewInvitationService(context), new TeamService(context));

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

    // ---------- StatusOf ----------

    /// <summary>Used, expired and outstanding are each derived from the invite's own timestamps.</summary>
    [Fact]
    public void StatusOfReportsUsedWhenUsedAtIsSet()
    {
        // Arrange
        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            UsedAt = DateTimeOffset.UtcNow,
        };

        // Assert
        IndexModel.StatusOf(invitation).Should().Be("Used");
    }

    [Fact]
    public void StatusOfReportsExpiredWhenPastExpiryAndUnused()
    {
        // Arrange
        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
        };

        // Assert
        IndexModel.StatusOf(invitation).Should().Be("Expired");
    }

    [Fact]
    public void StatusOfReportsOutstandingWhenUnusedAndNotYetExpired()
    {
        // Arrange
        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        // Assert
        IndexModel.StatusOf(invitation).Should().Be("Outstanding");
    }

    // ---------- TargetOf ----------

    /// <summary>A new-account invite (no team) reads as "New account".</summary>
    [Fact]
    public async Task TargetOfReportsNewAccountWhenNoTeamIsBound()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        IndexModel model = NewModel(context);
        await model.OnGetAsync(CancellationToken.None);

        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            TeamId = null,
        };

        // Assert
        model.TargetOf(invitation).Should().Be("New account");
    }

    /// <summary>A team-bound invite resolves to that team's name, once OnGetAsync has loaded team names.</summary>
    [Fact]
    public async Task TargetOfReportsTheBoundTeamsName()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        Team team = new() { Name = "Print Squad", CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        IndexModel model = NewModel(context);
        await model.OnGetAsync(CancellationToken.None);

        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            TeamId = team.Id,
        };

        // Assert
        model.TargetOf(invitation).Should().Be("Print Squad");
    }

    /// <summary>A team id that no longer resolves falls back to a numbered placeholder rather than throwing.</summary>
    [Fact]
    public async Task TargetOfFallsBackToANumberedPlaceholderForAnUnknownTeam()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        IndexModel model = NewModel(context);
        await model.OnGetAsync(CancellationToken.None);

        Invitation invitation = new()
        {
            HashedToken = "irrelevant",
            Email = "invitee@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            TeamId = 999,
        };

        // Assert
        model.TargetOf(invitation).Should().Be("Team #999");
    }

    // ---------- OnGetAsync ----------

    /// <summary>The list loads every invitation, newest first, matching the service.</summary>
    [Fact]
    public async Task OnGetAsyncPopulatesInvitationsNewestFirst()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);

        (Invitation first, _) = await invitationService.CreateAsync("first@example.com", null, 1, null, CancellationToken.None);
        first.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await context.SaveChangesAsync();

        (Invitation second, _) = await invitationService.CreateAsync("second@example.com", null, 1, null, CancellationToken.None);

        IndexModel model = new(invitationService, new TeamService(context));

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Invitations.Select(i => i.Id).Should().ContainInOrder(second.Id, first.Id);
    }

    // ---------- OnPostRevokeAsync ----------

    /// <summary>Revoking sets the status message and redirects back to the list.</summary>
    [Fact]
    public async Task OnPostRevokeAsyncRevokesAndRedirectsWithAStatusMessage()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService invitationService = NewInvitationService(context);

        (Invitation invitation, string plaintext) = await invitationService.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        IndexModel model = new(invitationService, new TeamService(context));

        // Act
        IActionResult result = await model.OnPostRevokeAsync(invitation.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("Invitation revoked.");

        (await invitationService.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should().BeNull("revoking soft-expires the invite");
    }
}
