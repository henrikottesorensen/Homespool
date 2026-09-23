// RemoveLoginAsync, RemovePasskeyAsync and UpdateUserAsync are transcribed from dotnet/aspnetcore at v10.0.12
// (src/Identity/Extensions.Core/src/UserManager.cs), changed as described on each.
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
/// address changed, removing a login or a passkey the account does not hold fails, and a closed
/// account's emailed tokens do not verify.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework reports removing a login that is not there as a success.</b> The store deletes
/// the row only if it finds one, and the manager then rotates the stamp and saves regardless, so a
/// caller acting on the result - setting a password in the same transaction, or telling the owner a
/// provider went - acts on a removal that did not happen. <see cref="RemoveLoginAsync"/> refuses
/// instead, before anything is written, and <see cref="RemovePasskeyAsync"/> does the same for a
/// passkey, which the framework treats alike.
/// </para>
/// <para>
/// <b>A write validates only what it changes.</b> The framework ends every write - a failed sign-in
/// counted or reset, a password, a login, a passkey, a recovery code, two-factor on or off - in
/// <see cref="UpdateUserAsync"/>, which ran every <see cref="IUserValidator{TUser}"/> over the whole
/// account first and saved nothing when one refused. A name valid when chosen can be refused later -
/// <see cref="UsernameValidator"/> compares it against every other account, and against rules that
/// can tighten - and the refusal landed on writes that had nothing to do with it: failed sign-ins not
/// counted, so the account never locked out; a recovery code not spent and its sign-in refused; a
/// passkey sign-in refused after its sign count was already saved. The validators check the username
/// and the address and nothing else, so <see cref="UpdateUserAsync"/> now runs them when either
/// changed, and a change of a name or an address is still refused as before, which is a failure the
/// person making it sees.
/// </para>
/// <para>
/// The framework's user-update metric is not recorded for the two removals: its meter is private to
/// <see cref="UserManager{TUser}"/>. The framework's own callers of <see cref="UpdateUserAsync"/> still
/// record it.
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

    /// <summary>The <see cref="IdentityError.Code"/> of a refused <see cref="RemovePasskeyAsync"/>.</summary>
    public const string PasskeyNotHeldCode = "PasskeyNotHeld";

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
    /// Fails with <see cref="PasskeyNotHeldCode"/>, writing nothing, when <paramref name="user"/> holds
    /// no passkey with <paramref name="credentialId"/>. The framework's store finds nothing, removes
    /// nothing, and the save after it reports success for a removal that did not happen.
    /// </para>
    /// <para>
    /// <b>The store saves the removal itself</b>, before the account row is saved, so a failure from
    /// that save still leaves the passkey gone. Two removals racing each other can both pass the check;
    /// the second then removes nothing and succeeds, as the framework would.
    /// </para>
    /// </remarks>
    public override async Task<IdentityResult> RemovePasskeyAsync(HSUser user, byte[] credentialId)
    {
        ThrowIfDisposed();
        IUserPasskeyStore<HSUser> passkeyStore = PasskeyStore();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        if (await passkeyStore.FindPasskeyAsync(user, credentialId, CancellationToken) is null)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = PasskeyNotHeldCode,
                Description = "This account has no such passkey.",
            });
        }

        await passkeyStore.RemovePasskeyAsync(user, credentialId, CancellationToken);

        return await UpdateUserAsync(user);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>False for a closed account, whatever the token.</b> Every emailed token is redeemed through
    /// here - the password reset, the address confirmation and the address change - and each is
    /// redeemed on a page nobody has to be signed in to reach, so the sign-in gate never sees them. A
    /// closed account answering a reset link would take a password it keeps when it is reopened.
    /// </para>
    /// <para>
    /// Here rather than on the pages because a new page redeeming a token gets the rule without
    /// knowing it exists, and the caller's answer is the one a wrong token gets, so nothing says the
    /// account is closed. The authenticator code does not come through here; the sign-in gate refuses
    /// it before it is compared.
    /// </para>
    /// </remarks>
    public override async Task<bool> VerifyUserTokenAsync(HSUser user, string tokenProvider, string purpose, string token)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.DeactivatedAt is not null)
        {
            Logger.LogInformation("An emailed token was refused because account {UserId} is closed.", user.Id);

            return false;
        }

        return await base.VerifyUserTokenAsync(user, tokenProvider, purpose, token);
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

    private IUserPasskeyStore<HSUser> PasskeyStore()
    {
        return Store as IUserPasskeyStore<HSUser> ??
               throw new NotSupportedException("The user store does not implement IUserPasskeyStore<HSUser>.");
    }

    private IUserLoginStore<HSUser> LoginStore()
    {
        return Store as IUserLoginStore<HSUser> ??
               throw new NotSupportedException("The user store does not implement IUserLoginStore<HSUser>.");
    }
}
