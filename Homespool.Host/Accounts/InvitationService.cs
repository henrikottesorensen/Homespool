using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
using Homespool.Model;
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

    /// <summary>
    /// Whether a row's <see cref="Invitation.Type"/> and <see cref="Invitation.RecoversUserId"/>
    /// agree: a recovery names an account, and a signup does not.
    /// </summary>
    /// <remarks>
    /// Nothing but this class writes invitations, and it always writes the two together, so a row
    /// failing this was written by something else - most plausibly a recovery issued before the
    /// column existed and given its default. Refused rather than resolved: either reading of such a
    /// row would be a guess about what an administrator meant. An expression rather than a method so
    /// the address lookup can run it in SQL.
    /// </remarks>
    private static readonly Expression<Func<Invitation, bool>> Coherent =
        i => (i.Type == InvitationType.Recovery && i.RecoversUserId != null) ||
             (i.Type == InvitationType.Signup && i.RecoversUserId == null);

    private static readonly Func<Invitation, bool> IsCoherent = Coherent.Compile();

    private readonly HomespoolDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly InvitationOptions _options;

    public InvitationService(HomespoolDbContext dbContext, TokenService tokenService, IOptionsSnapshot<InvitationOptions> options)
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
        // The address becomes an account's when the invitation is accepted, and would be refused
        // there - by which time it has been stored, listed and mailed. The page says so beside the
        // field; this is what holds whoever else comes to call it.
        if (!EmailAddresses.IsStorable(email))
        {
            throw new ArgumentException("The address holds a control or invisible character, or is too long to be one.", nameof(email));
        }

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
            Type = InvitationType.Signup,
        };

        _dbContext.Invitations.Add(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (invitation, plaintext);
    }

    /// <summary>
    /// Creates a <b>recovery</b> invite: one that gives <paramref name="userId"/> back to its owner
    /// rather than creating an account, returning it with its plaintext token as
    /// <see cref="CreateAsync"/> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The address it carries is the account's own</b>, taken by the caller from the account
    /// rather than typed, so a recovery cannot be aimed at a mailbox its subject does not hold. What
    /// the invite is bound to is the id; the address is where the link is sent and what the page
    /// shows.
    /// </para>
    /// <para>
    /// <b>It is the same token, lifetime and single-use rule as any other invite.</b> What differs is
    /// only what redeeming it does, so there is one thing to reason about at rest: a hashed token
    /// with an expiry and a <c>UsedAt</c> stamp.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account being recovered.</param>
    /// <param name="email">That account's address, where the link is sent.</param>
    /// <param name="clearsTwoFactor">Whether redeeming also clears the account's authenticator.</param>
    /// <param name="invitedBy">The administrator issuing it, recorded for audit.</param>
    /// <param name="expiresAt">Explicit expiry, or null for the configured default from now.</param>
    /// <param name="cancellationToken">Cancels the insert; nothing is persisted if it fires first.</param>
    public async Task<(Invitation invitation, string plaintextToken)> CreateRecoveryAsync(long userId,
                                                                                          string email,
                                                                                          bool clearsTwoFactor,
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
            TeamId = null,
            Type = InvitationType.Recovery,
            RecoversUserId = userId,
            ClearsTwoFactor = clearsTwoFactor,
        };

        _dbContext.Invitations.Add(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (invitation, plaintext);
    }

    /// <summary>
    /// Loads invite <paramref name="inviteUuid"/> and returns it only if it is outstanding (not used, not
    /// expired), of a type in <paramref name="accepts"/>, <b>and</b> <paramref name="plaintextToken"/>
    /// verifies against its stored hash. Returns <c>null</c> on any failure without distinguishing
    /// which — a wrong token, a used invite, an expired one, one of the wrong type and an unknown uuid
    /// are indistinguishable to the caller, so nothing here is an oracle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller names the types it can redeem.</b> Both types arrive through the same link, so a
    /// page that only creates accounts would otherwise be handed a recovery and create an account on
    /// it - spending the recovery without recovering anything.
    /// </para>
    /// <para>
    /// The returned entity is tracked by the request-scoped context, so a caller inside a transaction
    /// can pass it straight to <see cref="MarkUsedAsync"/> to spend it atomically.
    /// </para>
    /// </remarks>
    public async Task<Invitation?> ValidateAsync(Guid inviteUuid,
                                                 string? plaintextToken,
                                                 IReadOnlyCollection<InvitationType> accepts,
                                                 CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepts);

        if (string.IsNullOrEmpty(plaintextToken))
        {
            return null;
        }

        Invitation? invitation = await _dbContext.Invitations.SingleOrDefaultAsync(i => i.Uuid == inviteUuid, cancellationToken);

        if (invitation is null ||
            invitation.UsedAt is not null ||
            invitation.ExpiresAt <= DateTimeOffset.UtcNow ||
            !accepts.Contains(invitation.Type) ||
            !IsCoherent(invitation))
        {
            return null;
        }

        // VerifyToken returns false (never throws) on a malformed presented token; our stored hash is
        // well-formed, so the ArgumentException path cannot be reached here.
        return _tokenService.VerifyToken(plaintextToken, invitation.HashedToken) ? invitation : null;
    }

    /// <summary>
    /// Finds the newest outstanding invite bound to <paramref name="email"/> of a type in
    /// <paramref name="accepts"/>, or <c>null</c>.
    /// <b>This authenticates nobody.</b> Unlike <see cref="ValidateAsync"/> there is no token to
    /// verify, so the caller must already have established that the presented address belongs to the
    /// caller — see <c>OidcOptions.AllowInviteMatchByEmail</c>, which is the only thing that reaches
    /// here and does so only against a provider-verified address.
    /// </summary>
    /// <param name="email">The address to match, compared case-insensitively.</param>
    /// <param name="accepts">The types the caller can redeem; any other is passed over.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all, rather than the caller reusing <see cref="ValidateAsync"/>:</b> the
    /// stored hash is salted, so an invite cannot be located from a token — the uuid has to come from
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
    /// <b>The type is part of the query, not a check on the result</b>, so a newer invite of a type
    /// the caller cannot redeem does not hide an older one it can.
    /// </para>
    /// <para>
    /// <b>The address is compared in memory, over the outstanding invites.</b>
    /// <see cref="EmailAddresses.SameAddress"/> ignores case in any script while refusing the folds
    /// that turn one mailbox into another, and SQLite cannot express it: its <c>upper()</c> folds a-z
    /// only, which left a lowercase å never matching even itself, while a one-sided
    /// <see cref="string.ToUpperInvariant"/> matched a long s to a plain s - a different mailbox
    /// redeeming somebody else's invitation, behind an opt-in and an <c>email_verified</c> that vouch
    /// for the look-alike mailbox rather than the invited one. At one-to-tens of printers this table
    /// holds tens of rows, and the outstanding ones are fewer; an index on
    /// <see cref="Invitation.Email"/> would not serve this comparison, so there is none.
    /// </para>
    /// </remarks>
    public async Task<Invitation?> FindOutstandingForEmailAsync(string? email,
                                                                IReadOnlyCollection<InvitationType> accepts,
                                                                CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepts);

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string asserted = email.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Tracked, not AsNoTracking: the caller spends the one it gets inside its own transaction.
        List<Invitation> outstanding = await _dbContext.Invitations
                                                       .Where(i => i.UsedAt == null &&
                                                                   i.ExpiresAt > now &&
                                                                   accepts.Contains(i.Type))
                                                       .Where(Coherent)
                                                       .OrderByDescending(i => i.CreatedAt)
                                                       .ToListAsync(cancellationToken);

        return outstanding.FirstOrDefault(i => EmailAddresses.SameAddress(i.Email, asserted));
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
    /// Revokes invite <paramref name="inviteUuid"/> by expiring it now — a soft revoke that keeps the row
    /// for audit and needs no dedicated status column. A revoked invite reads as "expired". No-op if the
    /// uuid is unknown or the invite is already used/expired.
    /// </summary>
    public async Task RevokeAsync(Guid inviteUuid, CancellationToken cancellationToken)
    {
        Invitation? invitation = await _dbContext.Invitations.SingleOrDefaultAsync(i => i.Uuid == inviteUuid, cancellationToken);

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
