using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Issues, validates and revokes <see cref="Invitation"/>s. This is the single home for invite token
/// handling, so the admin create page and the accept page never duplicate the generate/hash/verify
/// dance — the token scheme is <see cref="TokenService"/> (PBKDF2/SHA-384), the same one that
/// protects printer registration tokens.
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

    private readonly HomespoolDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly InvitationOptions _options;

    public InvitationService(HomespoolDbContext dbContext, TokenService tokenService, IOptions<InvitationOptions> options)
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
    /// <param name="email">The address the invite is bound to; the invite is only redeemable for it.</param>
    /// <param name="teamId">
    /// The team to join, or <c>null</c> to mint a brand-new account with its own default team.
    /// </param>
    /// <param name="invitedBy">The <see cref="HSUser"/> id of the inviter, recorded for audit.</param>
    /// <param name="expiresAt">
    /// Explicit expiry, or <c>null</c> to use the configured default lifetime from now.
    /// </param>
    /// <param name="cancellationToken">Cancels the insert; nothing is persisted if it fires first.</param>
    public async Task<(Invitation invitation, string plaintextToken)> CreateAsync(string email,
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
    /// Finds an outstanding invite bound to <paramref name="email"/>, newest first, or <c>null</c>.
    /// <b>This authenticates nobody.</b> Unlike <see cref="ValidateAsync"/> there is no token to
    /// verify, so the caller must already have established that the presented address belongs to the
    /// caller — see <c>OidcOptions.AllowInviteMatchByEmail</c>, which is the only thing that reaches
    /// here and does so only against a provider-verified address.
    /// </summary>
    /// <param name="email">The address to match, compared case-insensitively.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all, rather than the caller reusing <see cref="ValidateAsync"/>:</b> the
    /// stored hash is salted, so an invite cannot be located from a token — the id has to come from
    /// the accept link. A caller arriving from an identity provider has neither, which is precisely
    /// what makes the address the only thing left to match on, and why the trade is documented on the
    /// option rather than here.
    /// </para>
    /// <para>
    /// <b>Newest first, deliberately.</b> Re-inviting an address that already has an invite outstanding
    /// is how an administrator corrects one — most usefully its <see cref="Invitation.TeamId"/> — so
    /// the later row is the one that expresses the current intention. The earlier one stays
    /// outstanding until it lapses; both are single-use, and spending either spends only itself.
    /// </para>
    /// <para>
    /// <b>No index on <see cref="Invitation.Email"/>, and that is a decision.</b> Adding one means
    /// regenerating the migration in place, which against a deployed appliance is the whole procedure
    /// for a migration against a deployed appliance. At one-to-tens of printers this table holds
    /// tens of rows and the scan
    /// is free; the moment it does not, the index is a separate and obvious change.
    /// </para>
    /// <para>
    /// <see cref="string.ToUpper()"/> translates to SQLite's <c>upper()</c>, which folds ASCII only —
    /// the same fold ASP.NET Identity's default normaliser applies to the addresses it stores, so the
    /// two agree. An address differing only in the case of a non-ASCII character would not match; no
    /// invite can be issued that way either, since the administrator types the address that is stored.
    /// </para>
    /// </remarks>
    public async Task<Invitation?> FindOutstandingForEmailAsync(string? email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string normalised = email.Trim().ToUpperInvariant();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _dbContext.Invitations
                               .Where(i => i.UsedAt == null
                                           && i.ExpiresAt > now
                                           && i.Email.ToUpper() == normalised)
                               .OrderByDescending(i => i.CreatedAt)
                               .FirstOrDefaultAsync(cancellationToken);
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
