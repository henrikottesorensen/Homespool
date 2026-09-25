using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// The framework's Entity Framework user store, except that the two second-factor secrets it keeps in
/// <c>AspNetUserTokens</c> are not stored as given: the authenticator key is Data Protection
/// ciphertext, and recovery codes are salted hashes (<see cref="RecoveryCodeHashes"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two treatments, because the two secrets are used differently.</b> The authenticator key has to
/// come back out - every code an authenticator app shows is computed from it - so it can only be
/// encrypted. A recovery code is only ever compared with what somebody types, and is shown once by the
/// page that mints it, so it can be hashed, and a hash needs no key.
/// </para>
/// <para>
/// <b>That difference is what a lost key ring costs.</b> The ring is protected by the Data Protection
/// certificate, and without it the authenticator key cannot be read: <see cref="GetAuthenticatorKeyAsync"/>
/// then answers <see langword="null"/>, as though none were set, rather than throwing. The account's
/// recovery codes do not depend on the ring, so one still signs it in, and enabling the authenticator
/// again mints a new key. Were the codes encrypted too, losing the certificate would lock every account
/// with two-factor out, with no way back in.
/// </para>
/// <para>
/// <b>What this defends is a copy of the database on its own</b> - a backup of the file, a
/// <c>docker cp</c>. The certificate's private key is encrypted under <c>CA_PASSPHRASE</c>, which is not
/// kept beside the database. Whoever holds the passphrase as well, or the running host, holds these
/// secrets too.
/// </para>
/// <para>
/// <b>Rows written by the framework's store are read as they are.</b> A plaintext key is returned
/// unchanged and replaced when the key is next reset; a plaintext code list still redeems, and redeeming
/// from it stores what is left as hashes.
/// </para>
/// </remarks>
public sealed class HSUserStore : UserStore<HSUser, IdentityRole<long>, HomespoolDbContext, long,
    IdentityUserClaim<long>, IdentityUserRole<long>, IdentityUserLogin<long>,
    IdentityUserToken<long>, IdentityRoleClaim<long>, IdentityUserPasskey<long>>
{
    /// <summary>
    /// Binds the authenticator key's ciphertext to its use. Never edit it: every stored key becomes
    /// unreadable.
    /// </summary>
    public const string AuthenticatorKeyPurpose = "Homespool.Accounts.AuthenticatorKey.v1";

    // The framework's names for the recovery-code row, private to its UserStoreBase. They have to match,
    // or every code the framework's store wrote would stop redeeming.
    private const string InternalLoginProvider = "[AspNetUserStore]";
    private const string RecoveryCodeTokenName = "RecoveryCodes";

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// How many times <see cref="CountAccessFailureAsync"/> goes round when both of its updates miss.
    /// Both miss only when the count falls between them, which takes a reset or a give-back - a right
    /// answer - landing in that gap every time; giving up answers "locked out" to an account that is
    /// not, so the ceiling is set where no run of right answers reaches it, as the limiter's is.
    /// </summary>
    private const int MaxCountPasses = 64;

    private readonly IDataProtector _authenticatorKeys;
    private readonly ILogger<HSUserStore> _logger;
    private readonly Dictionary<long, List<string>> _replacedStamps = [];

    public HSUserStore(HomespoolDbContext context,
                       IDataProtectionProvider dataProtection,
                       ILogger<HSUserStore> logger,
                       IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);

        _authenticatorKeys = dataProtection.CreateProtector(AuthenticatorKeyPurpose);
        _logger = logger;
    }

    /// <inheritdoc/>
    public override Task SetAuthenticatorKeyAsync(HSUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        return base.SetAuthenticatorKeyAsync(user, _authenticatorKeys.Protect(key), cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Remembers the stamp it replaces, for <see cref="StampsReplaced"/>. Every stamp the manager
    /// rotates is set through here, so the list is exactly the changes this scope - one request - made.
    /// </remarks>
    public override Task SetSecurityStampAsync(HSUser user, string stamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.SecurityStamp is { } replaced && !string.Equals(replaced, stamp, StringComparison.Ordinal))
        {
            if (!_replacedStamps.TryGetValue(user.Id, out List<string>? stamps))
            {
                stamps = [];
                _replacedStamps[user.Id] = stamps;
            }

            stamps.Add(replaced);
        }

        return base.SetSecurityStampAsync(user, stamp, cancellationToken);
    }

    /// <summary>
    /// The security stamps this store has replaced on <paramref name="user"/>, oldest first: the
    /// account's stamps before each change made through it. Empty when it made none.
    /// </summary>
    public IReadOnlyList<string> StampsReplaced(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _replacedStamps.TryGetValue(user.Id, out List<string>? stamps) ? stamps : [];
    }

    /// <summary>
    /// Whether <paramref name="user"/>'s username or address may differ from what was loaded, which
    /// decides whether <see cref="HSUserManager"/> runs the user validators on a save.
    /// </summary>
    /// <remarks>
    /// Compared ordinally against the context's original values, so a change of case counts. An entity
    /// the context is not tracking, or one being added, has nothing to compare with and counts as
    /// changed: the answer that fails safe is to validate.
    /// </remarks>
    public bool NameOrAddressChanged(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        EntityEntry<HSUser> entry = Context.Entry(user);

        if (entry.State is EntityState.Detached or EntityState.Added)
        {
            return true;
        }

        return !string.Equals(entry.Property(u => u.UserName).OriginalValue, user.UserName, StringComparison.Ordinal) ||
               !string.Equals(entry.Property(u => u.Email).OriginalValue, user.Email, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counts one failed sign-in on <paramref name="user"/>'s row, locking the account out until
    /// <paramref name="lockoutEnd"/> when that reaches <paramref name="maxFailedAttempts"/> - or, with
    /// <paramref name="onlyIfNotLockedOut"/>, refuses to count one while it is locked out at
    /// <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written as an update of the row, not a save of the entity, because the framework's save
    /// loses counts.</b> It adds one to the count in memory and saves the whole row under the
    /// concurrency stamp, so of N requests that loaded the row together one save lands and the rest
    /// fail on the stamp - and a failed save of a failed count is a guess that cost nothing.
    /// Thirty-nine parallel wrong passwords were measured leaving a count of four and no lockout.
    /// </para>
    /// <para>
    /// <b>Two updates, each conditional on what it changes</b>: one adds to a count still below the
    /// threshold, the other starts the lockout and returns the count to zero - the framework's
    /// arithmetic - on a count that reaches it. Which of them changed a row is what says whether this
    /// attempt was counted, imposed the lockout, or was refused. A reset between the two can leave
    /// both missing, so a miss on an account that is not locked out goes round again.
    /// </para>
    /// <para>
    /// <b>Refused inside a transaction</b>: a rollback would uncount the attempt.
    /// </para>
    /// </remarks>
    public async Task<AttemptTicket> CountAccessFailureAsync(HSUser user,
                                                             int maxFailedAttempts,
                                                             DateTimeOffset now,
                                                             DateTimeOffset lockoutEnd,
                                                             bool onlyIfNotLockedOut,
                                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        OutsideTransaction.Require(Context);

        // As the column will hold it, so the ticket compares equal to what is stored.
        DateTimeOffset end = DateTimeOffset.FromUnixTimeMilliseconds(lockoutEnd.ToUnixTimeMilliseconds());
        (string? loaded, string fresh, string other) = NextStamps(user);

        for (int pass = 0; pass < MaxCountPasses; pass++)
        {
            IQueryable<HSUser> open = Row(user);

            if (onlyIfNotLockedOut)
            {
                open = open.Where(u => !u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd <= now);
            }

            int counted = await open.Where(u => u.AccessFailedCount + 1 < maxFailedAttempts)
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1)
                                                                          .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                                        cancellationToken);

            if (counted > 0)
            {
                await SettleAsync(user, fresh, cancellationToken);

                return AttemptTicket.Counted(lockoutImposed: null);
            }

            int lockedOut = await open.Where(u => u.AccessFailedCount + 1 >= maxFailedAttempts)
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, 0)
                                                                            .SetProperty(u => u.LockoutEnd, end)
                                                                            .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                                          cancellationToken);

            if (lockedOut > 0)
            {
                await SettleAsync(user, fresh, cancellationToken);

                return AttemptTicket.Counted(end);
            }

            if (await LockedUntilAsync(user, cancellationToken) is { } until && until > now && onlyIfNotLockedOut)
            {
                return AttemptTicket.Refused(until - now);
            }
        }

        // The row kept changing under both updates, or is gone. Refusing is the answer that fails safe.
        return AttemptTicket.Refused(TimeSpan.Zero);
    }

    /// <summary>
    /// Gives back an attempt <see cref="CountAccessFailureAsync"/> counted, when the credential turned
    /// out right but the count is to stand: the attempt comes off it, and a lockout the attempt imposed
    /// - <paramref name="imposed"/> - is lifted with the count put back one short of
    /// <paramref name="maxFailedAttempts"/>, where the attempt found it.
    /// </summary>
    public async Task ReturnAccessAttemptAsync(HSUser user,
                                               int maxFailedAttempts,
                                               DateTimeOffset? imposed,
                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        (string? loaded, string fresh, string other) = NextStamps(user);

        if (imposed is { } ours &&
            await Row(user).Where(u => u.LockoutEnd == ours)
                           .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, maxFailedAttempts - 1)
                                                                 .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null)
                                                                 .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                               cancellationToken) > 0)
        {
            await SettleAsync(user, fresh, cancellationToken);

            return;
        }

        if (await Row(user).Where(u => u.AccessFailedCount > 0)
                           .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount - 1)
                                                                 .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                               cancellationToken) > 0)
        {
            await SettleAsync(user, fresh, cancellationToken);
        }
    }

    /// <summary>
    /// Clears <paramref name="user"/>'s failed count, and lifts the lockout <paramref name="imposed"/>
    /// names when it is still the one in force. Writes nothing when there is nothing to clear.
    /// </summary>
    /// <remarks>
    /// A lockout is otherwise left alone, as the framework's reset leaves it: a right credential proves
    /// this attempt, not that the guesses that locked the account were the owner's.
    /// </remarks>
    public async Task ClearAccessFailedCountAsync(HSUser user, DateTimeOffset? imposed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        (string? loaded, string fresh, string other) = NextStamps(user);

        int cleared = imposed is { } ours ?
            await Row(user).Where(u => u.AccessFailedCount != 0 || u.LockoutEnd == ours)
                           .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, 0)
                                                                 .SetProperty(u => u.LockoutEnd, u => u.LockoutEnd == ours ? null : u.LockoutEnd)
                                                                 .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                               cancellationToken) :
            await Row(user).Where(u => u.AccessFailedCount != 0)
                           .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, 0)
                                                                 .SetProperty(u => u.ConcurrencyStamp, u => u.ConcurrencyStamp == loaded ? fresh : other),
                                               cancellationToken);

        if (cleared > 0)
        {
            await SettleAsync(user, fresh, cancellationToken);
        }
    }

    private IQueryable<HSUser> Row(HSUser user)
    {
        return Context.Users.Where(u => u.Id == user.Id);
    }

    private async Task<DateTimeOffset?> LockedUntilAsync(HSUser user, CancellationToken cancellationToken)
    {
        return await Row(user).AsNoTracking().Select(u => u.LockoutEnd).SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The stamps a lockout write chooses between: <c>fresh</c> when the row still carries the stamp
    /// this context loaded, <c>other</c> when something else wrote it since.
    /// </summary>
    /// <remarks>
    /// The stamp moves on as any save moves it, so a save of a copy loaded before this write is refused
    /// rather than writing the old count back. Which value it takes says whether the tracked entity may
    /// adopt it.
    /// </remarks>
    private (string? loaded, string fresh, string other) NextStamps(HSUser user)
    {
        EntityEntry<HSUser> entry = Context.Entry(user);
        string? loaded = entry.State is EntityState.Detached ? user.ConcurrencyStamp : entry.Property(u => u.ConcurrencyStamp).OriginalValue;

        return (loaded, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Settles <paramref name="user"/> to the lockout columns as stored after a lockout write.
    /// </summary>
    /// <remarks>
    /// The count and the lockout end are adopted as stored, whatever else wrote them since, since those
    /// are the values the caller reads next. The stamp is adopted only when it is still
    /// <paramref name="fresh"/>: the row was as this context loaded it and nothing has written it after.
    /// Otherwise the entity is stale in columns this did not read, and a later save of it has to be
    /// refused as the framework would refuse it.
    /// </remarks>
    private async Task SettleAsync(HSUser user, string fresh, CancellationToken cancellationToken)
    {
        var stored = await Row(user).AsNoTracking()
                                    .Select(u => new { u.AccessFailedCount, u.LockoutEnd, u.ConcurrencyStamp })
                                    .SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return;
        }

        bool current = string.Equals(stored.ConcurrencyStamp, fresh, StringComparison.Ordinal);
        EntityEntry<HSUser> entry = Context.Entry(user);

        if (entry.State is EntityState.Detached)
        {
            user.AccessFailedCount = stored.AccessFailedCount;
            user.LockoutEnd = stored.LockoutEnd;

            if (current)
            {
                user.ConcurrencyStamp = stored.ConcurrencyStamp;
            }

            return;
        }

        Settle(entry.Property(u => u.AccessFailedCount), stored.AccessFailedCount);
        Settle(entry.Property(u => u.LockoutEnd), stored.LockoutEnd);

        if (current)
        {
            Settle(entry.Property(u => u.ConcurrencyStamp), stored.ConcurrencyStamp);
        }
    }

    /// <summary>Sets a tracked property to what the database holds, as though it had been loaded so.</summary>
    private static void Settle<T>(PropertyEntry<HSUser, T> property, T value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The framework mints a key as base32 in upper case, and Data Protection's output always carries
    /// lower case - its header encodes as <c>CfDJ8</c> - so a value made only of base32 characters was
    /// written by the framework's store and is the key itself.
    /// </remarks>
    public override async Task<string?> GetAuthenticatorKeyAsync(HSUser user, CancellationToken cancellationToken)
    {
        string? stored = await base.GetAuthenticatorKeyAsync(user, cancellationToken);

        if (stored is null || stored.All(c => Base32Alphabet.Contains(c, StringComparison.Ordinal)))
        {
            return stored;
        }

        try
        {
            return _authenticatorKeys.Unprotect(stored);
        }
        catch (CryptographicException exception)
        {
            _logger.LogError(exception,
                             "User {UserId} has an authenticator key this deployment cannot decrypt. Treating it as unset: a recovery code signs the account in, and enabling the authenticator again replaces the key.",
                             user.Id);

            return null;
        }
    }

    /// <inheritdoc/>
    public override Task ReplaceCodesAsync(HSUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        return SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName,
                             RecoveryCodeHashes.Create(recoveryCodes).ToStored(), cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<bool> RedeemCodeAsync(HSUser user, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(code);

        string stored = await StoredCodesAsync(user, cancellationToken);

        if (stored.Length == 0)
        {
            return false;
        }

        if (!RecoveryCodeHashes.IsHashed(stored))
        {
            List<string> codes = [.. stored.Split(';')];

            if (codes.RemoveAll(candidate => string.Equals(candidate, code, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            await ReplaceCodesAsync(user, codes, cancellationToken);

            return true;
        }

        if (Parse(user, stored) is not { } hashes || !hashes.TryRemove(code))
        {
            return false;
        }

        await SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, hashes.ToStored(), cancellationToken);

        return true;
    }

    /// <inheritdoc/>
    public override async Task<int> CountCodesAsync(HSUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);

        string stored = await StoredCodesAsync(user, cancellationToken);

        if (stored.Length == 0)
        {
            return 0;
        }

        return RecoveryCodeHashes.IsHashed(stored) ?
                   Parse(user, stored)?.Count ?? 0 :
                   stored.Split(';').Length;
    }

    private async Task<string> StoredCodesAsync(HSUser user, CancellationToken cancellationToken)
    {
        return await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? string.Empty;
    }

    private RecoveryCodeHashes? Parse(HSUser user, string stored)
    {
        RecoveryCodeHashes? hashes = RecoveryCodeHashes.Parse(stored);

        if (hashes is null)
        {
            // Treated as no codes: one this version cannot read cannot be redeemed either, and the
            // account's owner can mint a fresh set once signed in.
            _logger.LogError("User {UserId} has recovery codes stored in a form this version cannot read. Treating the account as having none.",
                             user.Id);
        }

        return hashes;
    }
}
