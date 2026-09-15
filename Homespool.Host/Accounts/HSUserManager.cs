// AccessFailedAsync and ResetAccessFailedCountAsync are transcribed from dotnet/aspnetcore at v10.0.12
// (src/Identity/Extensions.Core/src/UserManager.cs), with the final save changed as described below.
// Copyright (c) .NET Foundation, MIT licence.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// The framework's user manager, except that the failed-sign-in count and the lockout it starts are
/// saved without running the user validators.
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
/// <b>Nothing else skips validation.</b> Every other update - a name, an address, a password, a
/// security stamp - still goes through the validators and is still refused by them, which is a
/// failure the person making the change sees.
/// </para>
/// <para>
/// The saves here go straight to <see cref="UserManager{TUser}.Store"/>, which still refuses a write
/// whose concurrency stamp has moved. The framework's user-update metric is not recorded for these
/// two calls: its meter is private to <see cref="UserManager{TUser}"/>.
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

    private IUserLockoutStore<HSUser> LockoutStore()
    {
        return Store as IUserLockoutStore<HSUser>
               ?? throw new NotSupportedException("The user store does not implement IUserLockoutStore<HSUser>.");
    }
}
