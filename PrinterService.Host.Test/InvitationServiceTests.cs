using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

/// <summary>
/// Issuing, validating, spending and revoking invitations - the single home for the invite token
/// generate/hash/verify dance (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching <c>PrinterRegistrationTests</c>,
/// since these depend on provider behaviour for the timestamp comparisons in <c>ValidateAsync</c>.
/// </remarks>
public sealed class InvitationServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-invite-{Guid.NewGuid():N}.db");

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

    private static InvitationService NewService(PSDbContext context, int lifetimeHours = 48) =>
        new(context, new TokenService(), Options.Create(new InvitationOptions { LifetimeHours = lifetimeHours }));

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

    // ---------- CreateAsync ----------

    /// <summary>
    /// A new invite is bound to the given email, team and inviter, unused, and expires at the
    /// configured default lifetime from now when no explicit expiry is given.
    /// </summary>
    [Fact]
    public async Task CreateAsyncPersistsANewAccountInviteWithTheDefaultLifetime()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context, lifetimeHours: 48);

        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", teamId: null, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync();

        stored.Id.Should().Be(invitation.Id);
        stored.Email.Should().Be("invitee@example.com");
        stored.TeamId.Should().BeNull("no team was specified");
        stored.InvitedBy.Should().Be(1);
        stored.UsedAt.Should().BeNull();
        stored.HashedToken.Should().NotBe(plaintext, "the plaintext must never be stored");

        (stored.ExpiresAt - stored.CreatedAt).Should().Be(TimeSpan.FromHours(48));
        stored.ExpiresAt.Should().BeOnOrAfter(before.AddHours(48)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddHours(48));

        new TokenService().VerifyToken(plaintext, stored.HashedToken).Should().BeTrue();
    }

    /// <summary>
    /// An explicit expiry overrides the configured default lifetime.
    /// </summary>
    [Fact]
    public async Task CreateAsyncUsesTheExplicitExpiryWhenGiven()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context, lifetimeHours: 48);

        DateTimeOffset explicitExpiry = DateTimeOffset.UtcNow.AddHours(3);

        // Act
        (Invitation invitation, _) = await service.CreateAsync(
            "invitee@example.com", teamId: null, invitedBy: 1, expiresAt: explicitExpiry, CancellationToken.None);

        // Assert
        invitation.ExpiresAt.Should().Be(explicitExpiry);
    }

    /// <summary>
    /// An invite naming a team is bound to it, for the "join an existing team" accept shape.
    /// </summary>
    [Fact]
    public async Task CreateAsyncBindsAnInviteToTheGivenTeam()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        // Act
        (Invitation invitation, _) = await NewService(context).CreateAsync(
            "invitee@example.com", teamId: team.Id, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Assert
        invitation.TeamId.Should().Be(team.Id);
    }

    // ---------- ValidateAsync ----------

    /// <summary>
    /// The correct token against an outstanding invite validates and returns the row.
    /// </summary>
    [Fact]
    public async Task ValidateAsyncReturnsTheInvitationForACorrectTokenOnAnOutstandingInvite()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        // Act
        Invitation? validated = await service.ValidateAsync(invitation.Id, plaintext, CancellationToken.None);

        // Assert
        validated.Should().NotBeNull();
        validated!.Id.Should().Be(invitation.Id);
    }

    /// <summary>
    /// A wrong token, an unknown id, an expired invite, an already-used invite, and a null/empty token
    /// all fail the same way - <c>null</c>, never an exception - so none of them is an oracle for which
    /// reason the invite didn't validate.
    /// </summary>
    [Fact]
    public async Task ValidateAsyncReturnsNullWithoutThrowingForEveryFailureReason()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation outstanding, string outstandingToken) = await service.CreateAsync(
            "wrong-token@example.com", null, 1, null, CancellationToken.None);

        (Invitation expired, string expiredToken) = await service.CreateAsync(
            "expired@example.com", null, 1, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        (Invitation used, string usedToken) = await service.CreateAsync(
            "used@example.com", null, 1, null, CancellationToken.None);
        Invitation usedTracked = (await service.ValidateAsync(used.Id, usedToken, CancellationToken.None))!;
        await service.MarkUsedAsync(usedTracked, CancellationToken.None);

        // Assert
        (await service.ValidateAsync(outstanding.Id, "not-the-right-token", CancellationToken.None)).Should().BeNull();
        (await service.ValidateAsync(-1, outstandingToken, CancellationToken.None)).Should().BeNull("unknown id");
        (await service.ValidateAsync(expired.Id, expiredToken, CancellationToken.None)).Should().BeNull("expired");
        (await service.ValidateAsync(used.Id, usedToken, CancellationToken.None)).Should().BeNull("already used");
        (await service.ValidateAsync(outstanding.Id, null, CancellationToken.None)).Should().BeNull("null token");
        (await service.ValidateAsync(outstanding.Id, string.Empty, CancellationToken.None)).Should().BeNull("empty token");
    }

    // ---------- MarkUsedAsync ----------

    /// <summary>
    /// Spending an invite stamps <c>UsedAt</c>, after which the same token no longer validates - the
    /// single-use guarantee.
    /// </summary>
    [Fact]
    public async Task MarkUsedAsyncMakesTheInvitationSingleUse()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        Invitation tracked = (await service.ValidateAsync(invitation.Id, plaintext, CancellationToken.None))!;

        // Act
        await service.MarkUsedAsync(tracked, CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync(i => i.Id == invitation.Id);
        stored.UsedAt.Should().NotBeNull();

        (await service.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should().BeNull();
    }

    // ---------- RevokeAsync ----------

    /// <summary>
    /// Revoking soft-expires the invite, so it stops validating without a dedicated status column.
    /// </summary>
    [Fact]
    public async Task RevokeAsyncExpiresTheInviteImmediately()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        // Act
        await service.RevokeAsync(invitation.Id, CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync(i => i.Id == invitation.Id);
        stored.ExpiresAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow);

        (await service.ValidateAsync(invitation.Id, plaintext, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Revoking an unknown id is a no-op rather than a failure - there is nothing for the admin action
    /// to have raced against.
    /// </summary>
    [Fact]
    public async Task RevokeAsyncDoesNothingForAnUnknownId()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        // Act
        Func<Task> revoke = () => service.RevokeAsync(-1, CancellationToken.None);

        // Assert
        await revoke.Should().NotThrowAsync();
    }

    // ---------- ListAsync ----------

    /// <summary>
    /// The admin list is newest-first.
    /// </summary>
    [Fact]
    public async Task ListAsyncReturnsInvitationsNewestFirst()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation first, _) = await service.CreateAsync("first@example.com", null, 1, null, CancellationToken.None);
        first.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await context.SaveChangesAsync();

        (Invitation second, _) = await service.CreateAsync("second@example.com", null, 1, null, CancellationToken.None);

        // Act
        IReadOnlyList<Invitation> listed = await service.ListAsync(CancellationToken.None);

        // Assert
        listed.Select(i => i.Id).Should().ContainInOrder(second.Id, first.Id);
    }
}
