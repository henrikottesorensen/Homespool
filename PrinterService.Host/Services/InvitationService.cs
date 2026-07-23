using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Services;

/// <summary>
/// Issues, validates and revokes <see cref="Invitation"/>s. This is the single home for invite token
/// handling, so the admin create page and the accept page never duplicate the generate/hash/verify
/// dance — the token scheme is <see cref="TokenService"/> (PBKDF2/SHA3-384), the same one that
/// protects printer registration tokens (AGENT-NOTES phase-1.5 §15).
/// </summary>
/// <remarks>
/// The stored <see cref="Invitation.HashedToken"/> is salted, so an invite cannot be located by
/// hashing a presented token and looking it up. Callers therefore identify the row by
/// <see cref="Invitation.Id"/> (carried in the accept link) and let <see cref="ValidateAsync"/> verify
/// the token against that row.
/// </remarks>
public class InvitationService
{
    public const int InviteTokenLength = 32;
    
    private readonly PSDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly InvitationOptions _options;

    public InvitationService(PSDbContext dbContext, TokenService tokenService, IOptions<InvitationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _dbContext = dbContext;
        _tokenService = tokenService;
        _options = options.Value;
    }

    /// <summary>
    /// Creates and persists an invite bound to <paramref name="email"/>, returning the freshly minted
    /// invite alongside its <b>plaintext</b> token. The plaintext exists only in this return value —
    /// it is never stored — so the caller must mail or display it immediately; it cannot be recovered.
    /// </summary>
    /// <param name="teamId">
    /// The team to join, or <c>null</c> to mint a brand-new account with its own default team.
    /// </param>
    /// <param name="expiresAt">
    /// Explicit expiry, or <c>null</c> to use the configured default lifetime from now.
    /// </param>
    public async Task<(Invitation Invitation, string PlaintextToken)> CreateAsync(
        string email,
        int? teamId,
        long invitedBy,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string plaintext = _tokenService.GenerateToken(InviteTokenLength);

        Invitation invitation = new()
        {
            HashedToken = _tokenService.HashToken(plaintext),
            Email = email,
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now + _options.Lifetime,
            UsedAt = null,
            InvitedBy = invitedBy,
            TeamId = teamId,
        };

        _dbContext.Invitations.Add(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (invitation, plaintext);
    }

    /// <summary>
    /// Loads invite <paramref name="inviteId"/> and returns it only if it is outstanding (not used, not
    /// expired) <b>and</b> <paramref name="plaintextToken"/> verifies against its stored hash. Returns
    /// <c>null</c> on any failure without distinguishing which — a wrong token, a used invite, an
    /// expired one and an unknown id are indistinguishable to the caller, so nothing here is an oracle.
    /// </summary>
    /// <remarks>
    /// The returned entity is tracked by the request-scoped context, so a caller inside a transaction
    /// can pass it straight to <see cref="MarkUsedAsync"/> to spend it atomically.
    /// </remarks>
    public async Task<Invitation?> ValidateAsync(int inviteId, string? plaintextToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(plaintextToken))
        {
            return null;
        }

        Invitation? invitation = await _dbContext.Invitations.FindAsync([inviteId], cancellationToken);

        if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        // VerifyToken returns false (never throws) on a malformed presented token; our stored hash is
        // well-formed, so the ArgumentException path cannot be reached here.
        return _tokenService.VerifyToken(plaintextToken, invitation.HashedToken) ? invitation : null;
    }

    /// <summary>
    /// Stamps <see cref="Invitation.UsedAt"/> to spend the invite, making it single-use. Call this on a
    /// tracked invite (e.g. the one returned by <see cref="ValidateAsync"/>) inside the same transaction
    /// as the account creation it authorises, so a rolled-back accept leaves the invite outstanding.
    /// </summary>
    public Task MarkUsedAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        invitation.UsedAt = DateTimeOffset.UtcNow;

        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Revokes invite <paramref name="inviteId"/> by expiring it now — a soft revoke that keeps the row
    /// for audit and needs no dedicated status column. A revoked invite reads as "expired". No-op if the
    /// id is unknown or the invite is already used/expired.
    /// </summary>
    public async Task RevokeAsync(int inviteId, CancellationToken cancellationToken)
    {
        Invitation? invitation = await _dbContext.Invitations.FindAsync([inviteId], cancellationToken);

        if (invitation is null)
        {
            return;
        }

        invitation.ExpiresAt = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>All invites, newest first, for the admin list.</summary>
    public async Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Invitations
                               .AsNoTracking()
                               .OrderByDescending(i => i.CreatedAt)
                               .ToListAsync(cancellationToken);
    }
}
