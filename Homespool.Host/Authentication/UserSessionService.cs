using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Starts, checks and ends <see cref="UserSession"/>s - the one place that knows what makes a signed-in
/// browser's row live, so the per-request check, the revocations and the sweep cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live means three things, all read in the one query every cookie-authenticated request makes</b>:
/// the row has not expired, its stamp is the account's current one, and the passkey it names, if any,
/// is still on the account. A password change, a closed account or a revoked passkey needs no write
/// here to end a session; the next request finds it dead.
/// </para>
/// <para>
/// <b>The secret is hashed as <see cref="ApiTokenService.HashSecret"/> hashes a token's</b>: SHA-384,
/// unsalted, pinned. The same argument holds - 32 random bytes are not guessable, and a salted hash
/// could not be looked up.
/// </para>
/// </remarks>
public sealed class UserSessionService
{
    /// <summary>Bytes of randomness behind each session's secret.</summary>
    public const int SecretByteCount = 32;

    private readonly HomespoolDbContext _dbContext;
    private readonly TimeProvider _time;

    public UserSessionService(HomespoolDbContext dbContext, TimeProvider time)
    {
        _dbContext = dbContext;
        _time = time;
    }

    /// <summary>
    /// Records a new session for <paramref name="userId"/> and returns the secret its cookie is to
    /// carry. The secret exists only in this return value; the row keeps its hash.
    /// </summary>
    /// <param name="userId">The account signing in.</param>
    /// <param name="securityStamp">The account's stamp as the sign-in's principal carries it.</param>
    /// <param name="passkeyCredentialId">The passkey the sign-in proved, or null for any other credential.</param>
    /// <param name="expiresAt">When the cookie being issued expires.</param>
    /// <param name="cancellationToken">Cancels the insert; nothing is recorded if it fires first.</param>
    public async Task<string> StartAsync(long userId,
                                         string securityStamp,
                                         byte[]? passkeyCredentialId,
                                         DateTimeOffset expiresAt,
                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(securityStamp);

        string secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretByteCount));

        _dbContext.UserSessions.Add(new UserSession
        {
            UserId = userId,
            SecretHash = ApiTokenService.HashSecret(secret),
            SecurityStamp = securityStamp,
            PasskeyCredentialId = passkeyCredentialId,
            CreatedAt = _time.GetUtcNow(),
            ExpiresAt = expiresAt,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return secret;
    }

    /// <summary>
    /// Whether <paramref name="secret"/> names a live session of <paramref name="userId"/>'s - false when
    /// it was never recorded, has ended or expired, or died because the account or its passkey changed.
    /// </summary>
    public Task<bool> IsLiveAsync(long userId, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string hash = ApiTokenService.HashSecret(secret);

        return Live().AnyAsync(session => session.SecretHash == hash && session.UserId == userId, cancellationToken);
    }

    /// <summary>
    /// Brings the session <paramref name="secret"/> names up to the account's
    /// <paramref name="securityStamp"/>, so that a change this browser made to the account leaves it
    /// signed in while every other browser's row goes stale. Moved only from a stamp in
    /// <paramref name="replacedStamps"/> - the ones this request's own changes replaced - or when it
    /// already holds <paramref name="securityStamp"/>. False when there is no such row.
    /// </summary>
    /// <remarks>
    /// <b>The row's stamp is the condition, not just the secret.</b> A request is checked against its
    /// session when it arrives, and the account it then loads may already carry a stamp another browser
    /// set since - a password changed precisely to end this session. Moving the row by its secret alone
    /// would carry it onto that stamp, and the change meant to end it would be the one it survived.
    /// </remarks>
    public async Task<bool> RefreshAsync(string secret,
                                         string securityStamp,
                                         IReadOnlyCollection<string> replacedStamps,
                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(securityStamp);
        ArgumentNullException.ThrowIfNull(replacedStamps);

        string hash = ApiTokenService.HashSecret(secret);

        int updated = await _dbContext.UserSessions
                                      .Where(session => session.SecretHash == hash &&
                                                        (session.SecurityStamp == securityStamp ||
                                                         replacedStamps.Contains(session.SecurityStamp)))
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.SecurityStamp, securityStamp),
                                                          cancellationToken);

        return updated > 0;
    }

    /// <summary>
    /// Moves the expiry of the session <paramref name="secret"/> names to <paramref name="expiresAt"/>, as
    /// its cookie is renewed - when that session is live. A dead one stays dead rather than being given
    /// a longer life by a cookie that is about to be refused.
    /// </summary>
    public Task ExtendAsync(string secret, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string hash = ApiTokenService.HashSecret(secret);

        return Live().Where(session => session.SecretHash == hash)
                     .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ExpiresAt, expiresAt),
                                         cancellationToken);
    }

    /// <summary>Ends the session <paramref name="secret"/> names, if there is one.</summary>
    public Task EndAsync(string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string hash = ApiTokenService.HashSecret(secret);

        return _dbContext.UserSessions
                         .Where(session => session.SecretHash == hash)
                         .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary><paramref name="userId"/>'s live sessions, newest first.</summary>
    public async Task<IReadOnlyList<UserSession>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        return await Live().AsNoTracking()
                           .Where(session => session.UserId == userId)
                           .OrderByDescending(session => session.CreatedAt)
                           .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Ends session <paramref name="sessionUuid"/>. False if it does not exist <em>or</em> belongs to
    /// somebody else - the two are deliberately indistinguishable, as for an API token.
    /// </summary>
    public async Task<bool> RevokeAsync(long userId, Guid sessionUuid, CancellationToken cancellationToken)
    {
        int deleted = await _dbContext.UserSessions
                                      .Where(session => session.Uuid == sessionUuid && session.UserId == userId)
                                      .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    /// <summary>Ends every session <paramref name="userId"/> has, and says how many rows there were.</summary>
    public Task<int> RevokeAllAsync(long userId, CancellationToken cancellationToken)
    {
        return _dbContext.UserSessions
                         .Where(session => session.UserId == userId)
                         .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes every row that is no longer live, and says how many. Changes nothing a request could
    /// observe: a dead row is already refused.
    /// </summary>
    public Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IQueryable<UserSession> live = Live();

        return _dbContext.UserSessions
                         .Where(session => !live.Any(candidate => candidate.Id == session.Id))
                         .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>The sessions that still sign somebody in, as of now: the one definition of live.</summary>
    private IQueryable<UserSession> Live()
    {
        DateTimeOffset now = _time.GetUtcNow();
        IQueryable<HSUser> users = _dbContext.Users;
        IQueryable<IdentityUserPasskey<long>> passkeys = _dbContext.Set<IdentityUserPasskey<long>>();

        return _dbContext.UserSessions
                         .Where(session => session.ExpiresAt > now &&
                                           users.Any(user => user.Id == session.UserId && user.SecurityStamp == session.SecurityStamp) &&
                                           (session.PasskeyCredentialId == null ||
                                            passkeys.Any(passkey => passkey.UserId == session.UserId && passkey.CredentialId == session.PasskeyCredentialId)));
    }
}
