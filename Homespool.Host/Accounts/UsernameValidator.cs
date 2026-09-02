using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

using Squint;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// What a username may be: any letters and digits, from one script, plus <c>- . _</c>, and never
/// an address. UTS #39's identifier check, with this application's one rule on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs beside Identity's own <see cref="UserValidator{TUser}"/>, not instead of it.</b> That one
/// still refuses an empty name and a duplicate; its character check is switched off in
/// <see cref="IdentityConfiguration"/>, because a flat list of characters cannot say "any letter of
/// one alphabet", and this can. Registered through <c>AddUserValidator</c> in
/// <see cref="IdentityServices.AddHomespoolIdentity"/>, so the test harness applies it too.
/// </para>
/// <para>
/// <b>The profile decides which characters exist for this purpose.</b> UTS #39's General Security
/// Profile allows the letters of living alphabets - <c>þ ð æ ı ș ẞ</c> included - and the three
/// punctuation marks, and refuses archaic letters, compatibility digraphs, format characters such
/// as a zero-width joiner, whitespace, and both <c>@</c> and <c>+</c>. The <c>@</c> refusal is
/// also stated here in its own right, because it is load-bearing: sign-in takes a username or an
/// address in one field, and the two namespaces stay disjoint only while no username can be shaped
/// like an address. That must hold even if the profile ever changed.
/// </para>
/// <para>
/// <b>One script per name</b> is UTS #39's "highly restrictive" level: Latin, Cyrillic, Greek or
/// Japanese, but not two of them in one name, which is the cheap closure of the cross-script
/// homoglyph. Digits from two number systems are refused on the same ground. Within a script,
/// lookalikes are not refused at all - they are the <em>same</em> name, by
/// <see cref="SkeletonLookupNormalizer"/>, and the second is refused as a duplicate.
/// </para>
/// <para>
/// <b>An un-normalised name is refused rather than fixed.</b> A validator may not rewrite the value
/// it is checking; <see cref="Usernames.Normalise"/> is applied at each entry point instead, and this
/// check is what makes a forgotten entry point fail on its first accented name.
/// </para>
/// </remarks>
public sealed class UsernameValidator : IUserValidator<HSUser>
{
    private readonly IStringLocalizer<SharedResource> _localiser;

    public UsernameValidator(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <inheritdoc/>
    public async Task<IdentityResult> ValidateAsync(UserManager<HSUser> manager, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        string? name = await manager.GetUserNameAsync(user);

        if (string.IsNullOrEmpty(name))
        {
            // Identity's own validator reports the missing name; two errors for one omission help nobody.
            return IdentityResult.Success;
        }

        List<IdentityError> errors = [];

        IdentifierCheck check = Identifiers.Check(name, RestrictionLevel.HighlyRestrictive);

        bool addressShaped = name.Contains('@', StringComparison.Ordinal) || name.Contains('+', StringComparison.Ordinal);
        bool outsideProfile = (check.Problems & IdentifierProblems.OutsideProfile) != 0;
        bool notNormalised = !string.Equals(check.Normalized, name, StringComparison.Ordinal);

        if (addressShaped || outsideProfile || notNormalised)
        {
            errors.Add(new IdentityError
            {
                Code = "InvalidUserName",
                Description = _localiser["Account_InvalidUserName", name],
            });
        }

        if ((check.Problems & (IdentifierProblems.ExceedsRestrictionLevel | IdentifierProblems.MixedNumbers)) != 0)
        {
            errors.Add(new IdentityError
            {
                Code = "UserNameMixesScripts",
                Description = _localiser["Account_UsernameMixesScripts", name],
            });
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }
}
