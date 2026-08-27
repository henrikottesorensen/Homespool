using System;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Identity;

using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// A password hash belonging to nobody, to verify against when there is no account to verify
/// against - so that refusing an unknown identifier costs what refusing a wrong password costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sign-in form answers an unknown identifier and a wrong password identically on purpose</b> -
/// same message, same status - so that it cannot be used to discover which addresses and usernames
/// exist. That equality was written for what the response <i>says</i>, and the response also took a
/// measurably different amount of time to say it: a miss returned after two indexed lookups, where a
/// hit runs a full PBKDF2 verification first. Tens of milliseconds against microseconds is a
/// difference an unauthenticated caller can measure across the internet, so the sameness the form
/// promises has to cover the work and not only the wording.
/// </para>
/// <para>
/// <b>Static, because there is nothing here to configure or dispose.</b> The hash is one immutable
/// string that belongs to no account and outlives every request, and a class holding one value has
/// no business in the container: registering it bought a lifetime to get wrong, and the first attempt
/// did - a singleton cannot consume the scoped <see cref="IPasswordHasher{TUser}"/>, which fails
/// container validation at startup rather than at the call.
/// </para>
/// <para>
/// <b>The caller passes its own hasher in.</b> That keeps the expensive call on the same
/// <see cref="IPasswordHasher{TUser}"/> the real branch uses, so a substitute swapped in for a test
/// sees this verification too - which is what makes "both branches do the work" a property a test can
/// assert rather than a claim to read. The hash the call verifies against is this type's; the work is
/// the caller's hasher's.
/// </para>
/// <para>
/// <b>The residual, since it is the reason this was not a constant in the first place.</b> The hash
/// is built with <see cref="PasswordHasher{TUser}"/>'s defaults, and a PBKDF2 verification costs what
/// the <i>stored</i> hash's embedded iteration count says. Nothing configures
/// <see cref="PasswordHasherOptions"/> today, so the decoy and a real account cost the same;
/// configure it and they would not, and this type has to be revisited.
/// <c>PasswordHasherOptionsGuardTests</c> fails if that day arrives.
/// </para>
/// <para>
/// <b>What this does not claim.</b> The two paths are not constant-time and cannot be made so here:
/// a real sign-in also reads lockout state, may branch on a second factor, and writes a cookie when
/// it succeeds, while a miss does two lookups where a username hit does one. Those remainders are
/// microseconds beside a hash and below the jitter of any network an attacker measures through. The
/// point is to remove the order-of-magnitude term; calling the result constant-time would be worse
/// than the gap it describes.
/// </para>
/// </remarks>
public static class PasswordVerificationDecoy
{
    /// <summary>
    /// Identity's own hasher ignores the user it is handed; this exists because the interface asks
    /// for one.
    /// </summary>
    private static readonly HSUser Nobody = new();

    private static readonly string Hash;

    static PasswordVerificationDecoy()
    {
        // Random rather than a fixed string, so nothing about this hash is worth knowing. The answer
        // is discarded either way - a caller who somehow matched it would have matched a value
        // belonging to no account.
        Hash = new PasswordHasher<HSUser>()
            .HashPassword(Nobody, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against the decoy through <paramref name="hasher"/>, and
    /// discards the answer.
    /// </summary>
    public static void Verify(IPasswordHasher<HSUser> hasher, string? password)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        hasher.VerifyHashedPassword(Nobody, Hash, password ?? string.Empty);
    }
}
