using System;
using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;

namespace Homespool.Host.Localisation;

/// <summary>
/// Makes a validation attribute of our own say its message the way the framework's do: its
/// <see cref="ValidationAttribute.ErrorMessage"/> is a resource key, looked up when the page is built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this the key itself is what the page shows.</b> The framework localises an attribute's
/// message through an adapter, it has adapters only for its own attributes, and for any other it
/// quietly falls back to the unlocalised text - so <c>ErrorMessage = "Validation_..."</c> works on
/// <c>[StringLength]</c> and prints <c>Validation_...</c> beside the field on anything we write.
/// Nothing warns; the form still refuses.
/// </para>
/// <para>
/// Everything that is not ours goes to the framework's provider unchanged. <b>A new attribute of our
/// own needs an arm here</b>, and an end-to-end test that reads the words off the page is what
/// notices when it has none.
/// </para>
/// </remarks>
public sealed class ValidationAttributeAdapters : IValidationAttributeAdapterProvider
{
    private readonly ValidationAttributeAdapterProvider _framework = new();

    /// <inheritdoc />
    public IAttributeAdapter? GetAttributeAdapter(ValidationAttribute attribute, IStringLocalizer? stringLocalizer)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return attribute is StorableEmailAddressAttribute storable ?
            new MessageOnly<StorableEmailAddressAttribute>(storable, stringLocalizer) :
            _framework.GetAttributeAdapter(attribute, stringLocalizer);
    }

    /// <summary>
    /// The message, localised, and no rule for the browser: nothing in a browser knows which
    /// characters are refused, so the check is the server's alone and the form comes back with it.
    /// </summary>
    private sealed class MessageOnly<TAttribute>(TAttribute attribute, IStringLocalizer? stringLocalizer)
        : AttributeAdapterBase<TAttribute>(attribute, stringLocalizer)
        where TAttribute : ValidationAttribute
    {
        public override void AddValidation(ClientModelValidationContext context)
        {
        }

        public override string GetErrorMessage(ModelValidationContextBase validationContext)
        {
            ArgumentNullException.ThrowIfNull(validationContext);

            return GetErrorMessage(validationContext.ModelMetadata, validationContext.ModelMetadata.GetDisplayName());
        }
    }
}
