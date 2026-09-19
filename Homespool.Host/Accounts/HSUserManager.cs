// AccessFailedAsync, ResetAccessFailedCountAsync, RemoveLoginAsync and UpdateUserAsync are transcribed from
// dotnet/aspnetcore at v10.0.12 (src/Identity/Extensions.Core/src/UserManager.cs), changed as described on each.
// Copyright (c) .NET Foundation, MIT licence.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleBase;

using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// The framework's user manager, except that a save runs the user validators only when the username or
/// address changed, the failed-sign-in count is saved without them, and removing a login the account
/// does not hold fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework validates the whole account to save a counter.</b> Its <c>AccessFailedAsync</c>
/// and <c>ResetAccessFailedCountAsync</c> save through the same path as an edit to the account, which
/// runs every <see cref="IUserValidator{TUser}"/> first and saves nothing when one refuses. A name
/// that was valid when chosen can be refused later - <see cref="UsernameValidator"/> compares it
/// against every other account, and against rules that can tighten - and a refusal there is a result
/// nobody can act on: the count is not saved, the account never locks out, and nothing says so.
/// </para>
/// <para>
/// <b>Both methods, because skipping validation on the increment alone breaks sign-in.</b> A counted
/// wrong password leaves a count for the right password's reset to clear, and a reset that validated
/// would refuse the account's owner their own password. The framework's reset returns before saving
/// when the count is already zero, and so does this one.
/// </para>
/// <para>
/// <b>The framework reports removing a login that is not there as a success.</b> The store deletes
/// the row only if it finds one, and the manager then rotates the stamp and saves regardless, so a
/// caller acting on the result - setting a password in the same transaction, or telling the owner a
/// provider went - acts on a removal that did not happen. <see cref="RemoveLoginAsync"/> refuses
/// instead, before anything is written.
/// </para>
/// <para>
/// <b>Every other write validates only what it changes.</b> The framework ends every write - a
/// password, a login, a passkey, a recovery code, two-factor on or off - in
/// <see cref="UpdateUserAsync"/>, which runs every validator over the whole account first. The
/// validators check the username and the address and nothing else, so a write that changed neither
/// was being refused over a name that stopped validating for reasons of its own: a spent recovery
/// code not spent and its sign-in refused, a passkey sign-in refused after its sign count was already
/// saved. <see cref="UpdateUserAsync"/> now validates when either changed, and a change of a name or
/// an address is still refused as before, which is a failure the person making it sees.
/// </para>
/// <para>
/// The two counter saves go straight to <see cref="UserManager{TUser}.Store"/>, which still refuses a
/// write whose concurrency stamp has moved. The framework's user-update metric is not recorded for any
/// of the three overrides: its meter is private to <see cref="UserManager{TUser}"/>.
/// </para>
/// </remarks>
public sealed class HSUserManager : UserManager<HSUser>
{
    public HSUserManager(IUserStore<HSUser> store,
                         IOptions<IdentityOptions> optionsAccessor,
                         IPasswordHasher<HSUser> passwordHasher,
                         IEnumerable<IUserValidator<HSUser>> userValidators,
                         IEnumerable<IPasswordValidator<HSUser>> passwordValidators,
                         ILookupNormalizer keyNormalizer,
                         IdentityErrorDescriber errors,
                         IServiceProvider services,
                         ILogger<UserManager<HSUser>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
    }

    /// <summary>The <see cref="IdentityError.Code"/> of a refused <see cref="RemoveLoginAsync"/>.</summary>
    public const string LoginNotHeldCode = "LoginNotHeld";

    /// <inheritdoc/>
    /// <remarks>
    /// The lockout end is computed from the system clock, as the framework's
    /// <see cref="UserManager{TUser}.IsLockedOutAsync"/> reads it.
    /// </remarks>
    public override async Task<IdentityResult> AccessFailedAsync(HSUser user)
    {
        ThrowIfDisposed();
        IUserLockoutStore<HSUser> store = LockoutStore();
        ArgumentNullException.ThrowIfNull(user);

        // If this puts the user over the threshold for lockout, lock them out and reset the access failed count.
        int count = await store.IncrementAccessFailedCountAsync(user, CancellationToken);

        if (count >= Options.Lockout.MaxFailedAccessAttempts)
        {
            await store.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.Add(Options.Lockout.DefaultLockoutTimeSpan), CancellationToken);
            await store.ResetAccessFailedCountAsync(user, CancellationToken);
        }

        return await Store.UpdateAsync(user, CancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<IdentityResult> ResetAccessFailedCountAsync(HSUser user)
    {
        ThrowIfDisposed();
        IUserLockoutStore<HSUser> store = LockoutStore();
        ArgumentNullException.ThrowIfNull(user);

        if (await GetAccessFailedCountAsync(user) == 0)
        {
            return IdentityResult.Success;
        }

        await store.ResetAccessFailedCountAsync(user, CancellationToken);

        return await Store.UpdateAsync(user, CancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Fails with <see cref="LoginNotHeldCode"/>, writing nothing, when <paramref name="user"/> has no
    /// login matching both <paramref name="loginProvider"/> and <paramref name="providerKey"/>. The
    /// description is Identity's kind of message, in English: no page offers a login the account does
    /// not hold, so only a forged post or a second submission reaches it.
    /// </remarks>
    public override async Task<IdentityResult> RemoveLoginAsync(HSUser user, string loginProvider, string providerKey)
    {
        ThrowIfDisposed();
        IUserLoginStore<HSUser> loginStore = LoginStore();
        ArgumentNullException.ThrowIfNull(loginProvider);
        ArgumentNullException.ThrowIfNull(providerKey);
        ArgumentNullException.ThrowIfNull(user);

        IList<UserLoginInfo> held = await loginStore.GetLoginsAsync(user, CancellationToken);

        if (!held.Any(login => string.Equals(login.LoginProvider, loginProvider, StringComparison.Ordinal) &&
                               string.Equals(login.ProviderKey, providerKey, StringComparison.Ordinal)))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = LoginNotHeldCode,
                Description = "This account has no such login.",
            });
        }

        await loginStore.RemoveLoginAsync(user, loginProvider, providerKey, CancellationToken);

        // The framework's UpdateSecurityStampInternal and NewSecurityStamp, both private: twenty random
        // bytes as unpadded upper-case base32. The value is opaque; only that it changes matters.
        if (SupportsUserSecurityStamp)
        {
            IUserSecurityStampStore<HSUser> securityStore = Store as IUserSecurityStampStore<HSUser> ??
                throw new NotSupportedException("The user store does not implement IUserSecurityStampStore<HSUser>.");

            string stamp = Base32.Rfc4648.Encode(RandomNumberGenerator.GetBytes(20), padding: false);
            await securityStore.SetSecurityStampAsync(user, stamp, CancellationToken);
        }

        // Validated, normalised and saved as every other update is: UpdateUserAsync runs the user
        // validators, refreshes the normalised name and address, then Store.UpdateAsync.
        return await UpdateUserAsync(user);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The framework's body - validate, normalise, save - with the validation run only when
    /// <see cref="HSUserStore.NameOrAddressChanged"/> says the username or the address differs from
    /// what was loaded. That is what the validators check, and all they check; an unchanged name and
    /// address leave them nothing to say about this save. The original values, not the normalised
    /// columns, because a rename that only changes case normalises to the same key and must still be
    /// validated. A store that cannot say, or an entity its context is not tracking, is validated.
    /// </para>
    /// <para>
    /// The framework's validation also refuses an account with no security stamp, by throwing. That
    /// check stays on the path that skips the validators.
    /// </para>
    /// </remarks>
    protected override async Task<IdentityResult> UpdateUserAsync(HSUser user)
    {
        bool mayHaveChanged = Store is not HSUserStore store || store.NameOrAddressChanged(user);

        if (mayHaveChanged)
        {
            IdentityResult validated = await ValidateUserAsync(user);

            if (!validated.Succeeded)
            {
                return validated;
            }
        }
        else if (SupportsUserSecurityStamp)
        {
            // Throws for a missing stamp, as ValidateUserAsync's first check does.
            await GetSecurityStampAsync(user);
        }

        await UpdateNormalizedUserNameAsync(user);
        await UpdateNormalizedEmailAsync(user);

        return await Store.UpdateAsync(user, CancellationToken);
    }

    private IUserLoginStore<HSUser> LoginStore()
    {
        return Store as IUserLoginStore<HSUser> ??
               throw new NotSupportedException("The user store does not implement IUserLoginStore<HSUser>.");
    }

    private IUserLockoutStore<HSUser> LockoutStore()
    {
        return Store as IUserLockoutStore<HSUser> ??
               throw new NotSupportedException("The user store does not implement IUserLockoutStore<HSUser>.");
    }
}
