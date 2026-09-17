using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
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

    private readonly IDataProtector _authenticatorKeys;
    private readonly ILogger<HSUserStore> _logger;

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
