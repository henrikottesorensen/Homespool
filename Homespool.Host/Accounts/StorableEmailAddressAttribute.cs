using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Host.Accounts;

/// <summary>
/// On a field where an address is typed to be kept: says beside the field what
/// <see cref="EmailAddresses.IsStorable"/> would refuse later.
/// </summary>
/// <remarks>
/// For the message, not for the guarantee - that is <see cref="EmailAddressValidator"/> for an
/// account and <c>InvitationService</c> for an invitation. It goes beside <c>[EmailAddress]</c>, which
/// still checks the shape, and like it says nothing about an empty field: that is
/// <c>[Required]</c>'s to report. A field that only looks an address up does not need it; a dirty
/// address finds no account.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class StorableEmailAddressAttribute : ValidationAttribute
{
    /// <summary>The resource key of the refusal, which the validator shares.</summary>
    public const string MessageKey = "Validation_EmailNotStorable";

    public StorableEmailAddressAttribute()
    {
        ErrorMessage = MessageKey;
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        return value is null or "" || (value is string address && EmailAddresses.IsStorable(address));
    }
}
