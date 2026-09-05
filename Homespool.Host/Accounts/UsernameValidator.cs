using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Squint;
using Squint.Uts39;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// What a username may be: any letters and digits, from one script, plus <c>- . _</c>, never an
/// address, and never a cross-script lookalike of a name somebody already has. Squint's name
/// inspection, with this application's two rules on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs beside Identity's own <see cref="UserValidator{TUser}"/>, not instead of it.</b> That one
/// still refuses an empty name and an exact duplicate; its character check is switched off in
/// <see cref="IdentityConfiguration"/>, because a flat list of characters cannot say "any letter of
/// one alphabet", and this can. Registered through <c>AddUserValidator</c> in
/// <see cref="IdentityServices.AddHomespoolIdentity"/>, so the test harness applies it too.
/// </para>
/// <para>
/// <b>The inspection decides which characters exist for this purpose.</b> Its profile allows the
/// letters of living alphabets - <c>þ ð æ ı ș ẞ</c> included - and the three punctuation marks,
/// and refuses archaic letters, format characters such as a zero-width joiner, whitespace, and
/// both <c>@</c> and <c>+</c>. It also refuses a compatibility form - a ligature, a fullwidth
/// letter, a letter from a mathematical alphabet - rather than folding it to the letters it looks
/// like, which for a username is the right answer: nobody types one. The <c>@</c> refusal is also
/// stated here in its own right, because it is load-bearing: sign-in takes a username or an
/// address in one field, and the two namespaces stay disjoint only while no username can be shaped
/// like an address. That must hold even if the profile ever changed.
/// </para>
/// <para>
/// <b>One script per name</b> is <see cref="NamePolicy.OneScript"/>: Latin, Cyrillic, Greek or
/// Japanese, but not two of them in one name. Digits from two number systems are refused on the
/// same ground.
/// </para>
/// <para>
/// <b>A cross-script lookalike of an existing name is refused; a same-script one is not.</b> The
/// threat is a name that reads as somebody else's - Cyrillic <c>ѕсоре</c> against <c>scope</c> -
/// and Squint classifies a pair as whole-script, mixed-script or single-script confusable. The
/// first two are the impersonation shapes and are refused as looking like an existing name. The
/// third - <c>rnodern</c> beside <c>modern</c>, <c>þor</c> beside <c>por</c>, Ian beside Lan - is
/// what ASCII has always allowed, and it stays allowed: two real people can carry those names, and
/// merging them was tried once and taken out. This is a check at registration and rename against
/// every existing name, which for tens of accounts costs microseconds; it is deliberately not a
/// property of the lookup key, because Identity keys roles with the same normaliser and a key that
/// folds lookalikes turned <c>Admin</c> into a role nobody was in.
/// </para>
/// <para>
/// <b>Three messages, not one per finding.</b> The inspection words each finding with a position and
/// a character name, in English; the person sees the application's own sentences in their own
/// language instead - one for a name that mixes alphabets, one for a lookalike, one for everything
/// else - and the findings' detail goes nowhere, because a username field is not the place for
/// Unicode names.
/// </para>
/// <para>
/// <b>A name that is acceptable but not in its clean form is refused rather than fixed.</b> A
/// validator may not rewrite the value it is checking; <see cref="Usernames.Prepare"/> is applied at
/// each entry point instead, and this check is what makes a forgotten entry point fail on its first
/// accented name.
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

        Inspection inspection = Names.Inspect(name, NamePolicy.OneScript);

        bool mixesScripts = inspection.Findings.Any(f => f.Kind is FindingKind.MixedScripts or FindingKind.MixedDigits);
        bool otherwiseUnusable = inspection.Findings.Any(f => f.Kind is not (FindingKind.MixedScripts or FindingKind.MixedDigits));
        bool addressShaped = name.Contains('@', StringComparison.Ordinal) || name.Contains('+', StringComparison.Ordinal);
        bool notClean = inspection.IsAcceptable && !string.Equals(inspection.CleanForm, name, StringComparison.Ordinal);

        List<IdentityError> errors = [];

        if (otherwiseUnusable || addressShaped || notClean)
        {
            errors.Add(new IdentityError
            {
                Code = "InvalidUserName",
                Description = _localiser["Account_InvalidUserName", name],
            });
        }

        if (mixesScripts)
        {
            errors.Add(new IdentityError
            {
                Code = "UserNameMixesScripts",
                Description = _localiser["Account_UsernameMixesScripts", name],
            });
        }

        if (errors.Count == 0 && await LooksLikeAnotherAccountsNameAsync(manager, user, name))
        {
            errors.Add(new IdentityError
            {
                Code = "UserNameLooksLikeAnother",
                Description = _localiser["Account_UsernameLooksLikeAnother", name],
            });
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a whole-script or mixed-script confusable of any other
    /// account's name. Compared upper-cased, because sign-in is case-insensitive and a lookalike
    /// that differs only in case from what a person would type is still the lookalike.
    /// </summary>
    private static async Task<bool> LooksLikeAnotherAccountsNameAsync(UserManager<HSUser> manager, HSUser user, string name)
    {
        string candidate = name.ToUpperInvariant();

        List<string?> others = await manager.Users
                                            .Where(other => other.Id != user.Id)
                                            .Select(other => other.UserName)
                                            .ToListAsync();

        return others.Any(other => other is not null
                                   && Confusables.Classify(candidate, other.ToUpperInvariant())
                                       is ConfusableClass.WholeScript or ConfusableClass.MixedScript);
    }
}
