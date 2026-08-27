using System;

using Microsoft.AspNetCore.Identity;

using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Every deviation from Identity's default <see cref="IdentityOptions"/>, in one place.
/// </summary>
/// <remarks>
/// A method rather than a lambda in <c>Program</c> because the test harness builds its own Identity
/// services and must get the same rules: a unit test creating an account the real application would
/// refuse - a username shaped like an address, a second account on one address - would pass while
/// describing behaviour that cannot happen.
/// </remarks>
public static class IdentityConfiguration
{
    public static void Configure(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.SignIn.RequireConfirmedAccount = true;

        // A username is a name someone picks, so it may not be shaped like an address - see
        // HSUser.AllowedUsernameCharacters.
        options.User.AllowedUserNameCharacters = HSUser.AllowedUsernameCharacters;

        // Required because sign-in resolves an address to an account (Account/Login). Until the
        // username was decoupled this held for free, the address being the username; now nothing else
        // enforces it, and two accounts sharing an address would make that lookup pick one of them
        // arbitrarily.
        options.User.RequireUniqueEmail = true;
    }
}
