using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Refuses an account whose address is not one to keep, whichever page is creating or changing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here because this is where every address on an account passes.</b> First-time setup, an
/// accepted invitation, a provider sign-up and a confirmed change of address all end in the user
/// manager creating or updating the account, and it runs its validators each time - so the rule
/// holds for a page nobody has written yet. The forms say the same thing sooner, for the sake of a
/// message beside the field; they are not what keeps a bad address out.
/// </para>
/// <para>
/// A missing address is left to the framework's own validator, which reports it: two errors for one
/// omission help nobody.
/// </para>
/// </remarks>
public sealed class EmailAddressValidator : IUserValidator<HSUser>
{
    /// <summary>The code on the error, for a caller that tells refusals apart.</summary>
    public const string ErrorCode = "EmailNotStorable";

    private readonly IStringLocalizer<SharedResource> _localiser;

    public EmailAddressValidator(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <inheritdoc />
    public async Task<IdentityResult> ValidateAsync(UserManager<HSUser> manager, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        string? address = await manager.GetEmailAsync(user);

        if (string.IsNullOrEmpty(address) || EmailAddresses.IsStorable(address))
        {
            return IdentityResult.Success;
        }

        return IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = _localiser[StorableEmailAddressAttribute.MessageKey],
        });
    }
}
