using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Host.Services;

/// <summary>
/// On a field holding text that is shown back to people: refuses what
/// <see cref="PrintableText.IsPrintable(string)"/> would, and says so beside the field.
/// </summary>
/// <remarks>
/// Says nothing about an empty field, which is <c>[Required]</c>'s to report. What counts as
/// unprintable is <see cref="PrintableText"/>'s to say; this only asks.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class PrintableTextAttribute : ValidationAttribute
{
    /// <summary>The resource key of the refusal.</summary>
    public const string MessageKey = "Validation_TextNotPrintable";

    public PrintableTextAttribute()
    {
        ErrorMessage = MessageKey;
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        return value is null or "" || (value is string text && PrintableText.IsPrintable(text));
    }
}
