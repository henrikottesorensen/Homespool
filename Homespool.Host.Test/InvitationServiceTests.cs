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
using Homespool.Host.PrusaConnect;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Issuing, validating, spending and revoking invitations - the single home for the invite token
/// generate/hash/verify dance.
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching <c>PrinterRegistrationTests</c>,
/// since these depend on provider behaviour for the timestamp comparisons in <c>ValidateAsync</c>.
/// </remarks>
public sealed class InvitationServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-invite-{Guid.NewGuid():N}.db");

    private static InvitationService NewService(HomespoolDbContext context, int lifetimeHours = 48)
    {
        return new(context, new TokenService(), TestOptions.Snapshot(new InvitationOptions { LifetimeHours = lifetimeHours }));
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
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context, lifetimeHours: 48);

        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", teamId: null, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync(TestContext.Current.CancellationToken);

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
        await using HomespoolDbContext context = await MigratedContextAsync();
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
        await using HomespoolDbContext context = await MigratedContextAsync();
        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        (Invitation invitation, _) = await NewService(context).CreateAsync(
            "invitee@example.com", teamId: team.Id, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Assert
        invitation.TeamId.Should().Be(team.Id);
    }

    /// <summary>
    /// An invitation's address becomes an account's, so one that would be refused there is refused
    /// here, before it is stored, listed and mailed - whatever is calling.
    /// </summary>
    [Fact]
    public async Task AnInvitationIsNotCreatedForAnAddressThatIsNotKept()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        Func<Task> act = () => NewService(context).CreateAsync(
            "invitee\u202E@example.com", teamId: null, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        (await context.Invitations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ---------- ValidateAsync ----------

    /// <summary>
    /// The correct token against an outstanding invite validates and returns the row.
    /// </summary>
    [Fact]
    public async Task ValidateAsyncReturnsTheInvitationForACorrectTokenOnAnOutstandingInvite()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        // Act
        Invitation? validated = await service.ValidateAsync(invitation.Uuid, plaintext, [InvitationType.Signup], CancellationToken.None);

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
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation outstanding, string outstandingToken) = await service.CreateAsync(
            "wrong-token@example.com", null, 1, null, CancellationToken.None);

        (Invitation expired, string expiredToken) = await service.CreateAsync(
            "expired@example.com", null, 1, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        (Invitation used, string usedToken) = await service.CreateAsync(
            "used@example.com", null, 1, null, CancellationToken.None);
        Invitation usedTracked = (await service.ValidateAsync(used.Uuid, usedToken, [InvitationType.Signup], CancellationToken.None))!;
        await service.MarkUsedAsync(usedTracked, CancellationToken.None);

        // Assert
        (await service.ValidateAsync(outstanding.Uuid, "not-the-right-token", [InvitationType.Signup], CancellationToken.None)).Should().BeNull();
        (await service.ValidateAsync(Guid.NewGuid(), outstandingToken, [InvitationType.Signup], CancellationToken.None)).Should().BeNull("unknown uuid");
        (await service.ValidateAsync(expired.Uuid, expiredToken, [InvitationType.Signup], CancellationToken.None)).Should().BeNull("expired");
        (await service.ValidateAsync(used.Uuid, usedToken, [InvitationType.Signup], CancellationToken.None)).Should().BeNull("already used");
        (await service.ValidateAsync(outstanding.Uuid, null, [InvitationType.Signup], CancellationToken.None)).Should().BeNull("null token");
        (await service.ValidateAsync(outstanding.Uuid, string.Empty, [InvitationType.Signup], CancellationToken.None)).Should().BeNull("empty token");
    }

    /// <summary>
    /// A recovery's token is as good as any invite's, so what stops a page that only creates accounts
    /// from being handed one is the types it says it accepts - and a refusal on type looks exactly
    /// like every other refusal.
    /// </summary>
    [Fact]
    public async Task ValidateAsyncReturnsARecoveryOnlyToACallerThatAcceptsOne()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation recovery, string token) = await service.CreateRecoveryAsync(
            42, "owner@example.com", clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Act
        Invitation? signupOnly = await service.ValidateAsync(recovery.Uuid, token, [InvitationType.Signup], CancellationToken.None);
        Invitation? either = await service.ValidateAsync(
            recovery.Uuid, token, [InvitationType.Signup, InvitationType.Recovery], CancellationToken.None);

        // Assert
        signupOnly.Should().BeNull();
        either.Should().NotBeNull();
        either!.Type.Should().Be(InvitationType.Recovery);
    }

    /// <summary>The same in the other direction: a caller that only redeems recoveries is not handed a signup.</summary>
    [Fact]
    public async Task ValidateAsyncDoesNotReturnASignupToACallerThatOnlyAcceptsRecoveries()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation signup, string token) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        // Act
        Invitation? validated = await service.ValidateAsync(signup.Uuid, token, [InvitationType.Recovery], CancellationToken.None);

        // Assert
        validated.Should().BeNull();
        signup.Type.Should().Be(InvitationType.Signup);
    }

    /// <summary>
    /// A row whose type and <c>RecoversUserId</c> disagree is refused by both lookups, whichever
    /// types the caller accepts.
    /// </summary>
    /// <remarks>
    /// The signup-with-an-account case is the one a deployment can actually hold: a recovery issued
    /// before the column existed, labelled a signup by its default. Handing it to a caller that
    /// creates accounts is the defect this column exists to prevent, so the default must not
    /// reintroduce it.
    /// </remarks>
    [Theory]
    [InlineData(InvitationType.Signup, 42L)]
    [InlineData(InvitationType.Recovery, null)]
    public async Task AnInviteWhoseTypeAndAccountDisagreeIsRefused(InvitationType type, long? recoversUserId)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);
        TokenService tokens = new();
        string token = tokens.GenerateToken(InvitationService.InviteTokenLength);

        Invitation row = new()
        {
            HashedToken = tokens.HashToken(token),
            Type = type,
            Email = "owner@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            RecoversUserId = recoversUserId,
        };
        context.Invitations.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        InvitationType[] either = [InvitationType.Signup, InvitationType.Recovery];

        // Act
        Invitation? validated = await service.ValidateAsync(row.Uuid, token, either, CancellationToken.None);
        Invitation? found = await service.FindOutstandingForEmailAsync("owner@example.com", either, CancellationToken.None);

        // Assert
        validated.Should().BeNull();
        found.Should().BeNull();
    }

    /// <summary>
    /// A row written without a type reads back as a signup. That default is what a deployment's
    /// existing rows receive when the column is added to them, so it is pinned here rather than
    /// left to whatever the migration happened to say.
    /// </summary>
    [Fact]
    public async Task ARowWrittenWithoutATypeIsASignup()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Invitations\" (\"Uuid\", \"HashedToken\", \"Email\", \"CreatedAt\", \"ExpiresAt\", \"InvitedBy\", \"ClearsTwoFactor\") " +
            "VALUES ('6f1c1d2e-0000-4000-8000-000000000001', 'hash', 'old@example.com', 0, 0, 1, 0)",
            TestContext.Current.CancellationToken);

        // Assert
        Invitation stored = await context.Invitations.SingleAsync(TestContext.Current.CancellationToken);
        stored.Type.Should().Be(InvitationType.Signup);
    }

    // ---------- FindOutstandingForEmailAsync ----------

    /// <summary>
    /// The address door passes over a recovery sent to the address it matched: that invite names an
    /// account, and a caller that only creates accounts has nothing it could do with it.
    /// </summary>
    [Fact]
    public async Task FindOutstandingForEmailAsyncPassesOverATypeTheCallerDoesNotAccept()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        await service.CreateRecoveryAsync(
            42, "owner@example.com", clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Act
        Invitation? found = await service.FindOutstandingForEmailAsync("owner@example.com", [InvitationType.Signup], CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>
    /// A newer invite of a type the caller cannot redeem does not hide an older one it can - the type
    /// is filtered after the newest-first order, not by taking the newest and then checking it.
    /// </summary>
    [Fact]
    public async Task FindOutstandingForEmailAsyncIsNotHiddenByANewerInviteOfAnotherType()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation signup, _) = await service.CreateAsync("owner@example.com", null, 1, null, CancellationToken.None);
        await service.CreateRecoveryAsync(
            42, "owner@example.com", clearsTwoFactor: false, invitedBy: 1, expiresAt: null, CancellationToken.None);

        // Act
        Invitation? found = await service.FindOutstandingForEmailAsync("OWNER@example.com", [InvitationType.Signup], CancellationToken.None);

        // Assert
        found.Should().NotBeNull();
        found!.Id.Should().Be(signup.Id);
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
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        Invitation tracked = (await service.ValidateAsync(invitation.Uuid, plaintext, [InvitationType.Signup], CancellationToken.None))!;

        // Act
        await service.MarkUsedAsync(tracked, CancellationToken.None);

        // Assert
        Invitation stored =
            await context.Invitations.SingleAsync(i => i.Id == invitation.Id, TestContext.Current.CancellationToken);
        stored.UsedAt.Should().NotBeNull();

        (await service.ValidateAsync(invitation.Uuid, plaintext, [InvitationType.Signup], CancellationToken.None)).Should().BeNull();
    }

    // ---------- RevokeAsync ----------

    /// <summary>
    /// Revoking soft-expires the invite, so it stops validating without a dedicated status column.
    /// </summary>
    [Fact]
    public async Task RevokeAsyncExpiresTheInviteImmediately()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation invitation, string plaintext) = await service.CreateAsync(
            "invitee@example.com", null, 1, null, CancellationToken.None);

        // Act
        await service.RevokeAsync(invitation.Uuid, CancellationToken.None);

        // Assert
        Invitation stored =
            await context.Invitations.SingleAsync(i => i.Id == invitation.Id, TestContext.Current.CancellationToken);
        stored.ExpiresAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow);

        (await service.ValidateAsync(invitation.Uuid, plaintext, [InvitationType.Signup], CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Revoking an unknown uuid is a no-op rather than a failure - there is nothing for the admin action
    /// to have raced against.
    /// </summary>
    [Fact]
    public async Task RevokeAsyncDoesNothingForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        // Act
        Func<Task> revoke = () => service.RevokeAsync(Guid.NewGuid(), CancellationToken.None);

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
        await using HomespoolDbContext context = await MigratedContextAsync();
        InvitationService service = NewService(context);

        (Invitation first, _) = await service.CreateAsync("first@example.com", null, 1, null, CancellationToken.None);
        first.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (Invitation second, _) = await service.CreateAsync("second@example.com", null, 1, null, CancellationToken.None);

        // Act
        IReadOnlyList<Invitation> listed = await service.ListAsync(CancellationToken.None);

        // Assert
        listed.Select(i => i.Id).Should().ContainInOrder(second.Id, first.Id);
    }
}
