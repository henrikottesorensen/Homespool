using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// Mints, lists, revokes and resolves <see cref="ApiToken"/>s — the single home for the personal
/// access token scheme, so the management page and the authentication handler never duplicate the
/// generate/hash/look-up dance. Design and rejected alternatives: <c>notes/api-tokens.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="PrusaConnect.TokenService"/>.</b> That one salts and stretches with
/// PBKDF2, which is right for a 20-character printer token but wrong here for two reasons: a salted
/// hash cannot be indexed, so resolving a bearer credential would mean scanning every row and
/// verifying each; and stretching defends guessable secrets, which 32 CSPRNG bytes are not. Against a
/// leaked hash column an attacker faces 2^256 either way.
/// </para>
/// <para>
/// <b>The secret is hashed as text, never decoded.</b> The presented credential goes into SHA-384 as
/// its UTF-8 bytes, so there is no base64 parsing on the request path and therefore no malformed-input
/// branch to get wrong — a credential of the wrong shape simply hashes to something no row carries.
/// </para>
/// </remarks>
public class ApiTokenService
{
    /// <summary>
    /// Marks the credential as ours. It makes a leaked token greppable (<c>hs_[A-Za-z0-9_-]{43}</c>)
    /// and doubles as a free reject before anything is hashed.
    /// </summary>
    /// <remarks>
    /// Where it earns its keep is wherever a token is stored in plaintext — a script, a <c>.env</c>, a
    /// CI secret, pasted into a chat — which is where leaked credentials are actually found, rather
    /// than in the <c>Authorization</c> header it also appears in.
    /// </remarks>
    public const string Prefix = "hs_";

    /// <summary>
    /// Bytes of randomness behind each token. 32 is what makes a single fast hash defensible instead
    /// of a slow one; nothing in the protocol constrains it (firmware's 20-character token buffer and
    /// the 28-character transfer-hash ceiling are both elsewhere).
    /// </summary>
    public const int SecretByteCount = 32;

    /// <summary>Length of the base64url secret, which <see cref="SecretByteCount"/> fixes at 43.</summary>
    public const int SecretLength = 43;

    private readonly HomespoolDbContext _dbContext;

    public ApiTokenService(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Hashes a token's secret into the form stored in <see cref="ApiToken.TokenHash"/> and indexed
    /// for lookup.
    /// </summary>
    /// <remarks>
    /// <b>SHA-384 is pinned, not chosen at runtime.</b> The hash here is a <em>lookup key</em>, so an
    /// algorithm that varied by host would be worse than merely inconsistent: rows written on one
    /// machine would simply not be found on another, and every token would fail authentication after
    /// a move or a base-image change — silently, and looking exactly like revocation.
    /// </remarks>
    public static string HashSecret(string secret)
    {
        byte[] hash = SHA384.HashData(Encoding.UTF8.GetBytes(secret));

        return Base64Url.EncodeToString(hash);
    }

    /// <summary>
    /// Creates a token for <paramref name="userId"/>, returning it alongside the <b>plaintext</b>
    /// credential. The plaintext exists only in this return value — only its hash is stored — so the
    /// caller must show it immediately; it cannot be recovered afterwards, by anyone, by construction.
    /// </summary>
    /// <param name="userId">The user the token acts as. It inherits their rights in full.</param>
    /// <param name="name">What the owner calls it, so they know which one to revoke.</param>
    /// <param name="cancellationToken">Cancels the insert; nothing is persisted if it fires first.</param>
    public async Task<(ApiToken token, string plaintext)> CreateAsync(long userId, string name, CancellationToken cancellationToken)
    {
        string secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretByteCount));

        ApiToken token = new()
        {
            UserId = userId,
            TokenHash = HashSecret(secret),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.ApiTokens.Add(token);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (token, Prefix + secret);
    }

    /// <summary>This user's own tokens, newest first. Never anyone else's — there is no listing of all.</summary>
    public async Task<IReadOnlyList<ApiToken>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.ApiTokens
                               .Where(t => t.UserId == userId)
                               .OrderByDescending(t => t.CreatedAt)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes token <paramref name="tokenId"/>, which is what revocation is here. Returns false if it
    /// does not exist <em>or</em> belongs to someone else — the two are deliberately indistinguishable,
    /// so this cannot be used to probe for other people's token ids.
    /// </summary>
    public async Task<bool> RevokeAsync(long userId, long tokenId, CancellationToken cancellationToken)
    {
        // Ownership is in the query rather than checked after loading: a token belonging to another
        // user is not found at all, so there is no path on which the wrong row reaches the delete.
        ApiToken? token = await _dbContext.ApiTokens
                                          .SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, cancellationToken);

        if (token is null)
        {
            return false;
        }

        _dbContext.ApiTokens.Remove(token);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Revokes every token belonging to <paramref name="userId"/>, returning how many there were.
    /// Called when a password changes: see the pages that do it for why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bulk and untracked, like <c>TelemetryRetentionService</c>'s sweep - there is nothing to load,
    /// and the count is the only thing the caller wants. <b>It still participates in an ambient
    /// transaction</b>, which matters here: the callers wrap this together with the password change so
    /// that neither can land without the other.
    /// </para>
    /// <para>
    /// Deliberately not "revoke tokens created before now": there is no window to preserve. If the
    /// password is being changed because the account is compromised, a token the attacker minted a
    /// second ago is exactly the one that has to go.
    /// </para>
    /// </remarks>
    public async Task<int> RevokeAllForUserAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.ApiTokens
                               .Where(t => t.UserId == userId)
                               .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a presented credential — the value after <c>Bearer </c> — to its token row, or null if
    /// it is not one of ours. <b>Finding the row is the verification</b>: the hash is unsalted and
    /// indexed, so a hit means the presented secret's preimage matched.
    /// </summary>
    /// <remarks>
    /// The prefix and length are checked first, so a credential belonging to some other scheme costs a
    /// string comparison rather than a hash and a query. No constant-time comparison appears here
    /// because there is no comparison — the index lookup is the whole verification, and nobody should
    /// "fix" that by adding one.
    /// </remarks>
    public async Task<ApiToken?> FindByCredentialAsync(string? credential, CancellationToken cancellationToken)
    {
        if (credential is null ||
            credential.Length != Prefix.Length + SecretLength ||
            !credential.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string hash = HashSecret(credential[Prefix.Length..]);

        return await _dbContext.ApiTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }
}
