namespace Homespool.Host.Services;

/// <summary>
/// One field of a partial update: either absent (leave the stored value alone, i.e. <c>default</c>)
/// or present with a value, which may be <c>null</c> (clear it). A plain <c>string?</c> cannot say
/// which, and collapsing the two made a PATCH that omitted a field wipe it.
/// </summary>
/// <typeparam name="T">The field's value type.</typeparam>
/// <param name="IsSet">Whether the request carried the field at all.</param>
/// <param name="Value">The value to store when <paramref name="IsSet"/>; ignored otherwise.</param>
public readonly record struct PatchField<T>(bool IsSet, T? Value);

/// <summary>Constructors for <see cref="PatchField{T}"/>.</summary>
public static class PatchField
{
    /// <summary>A field the request carried, with <paramref name="value"/> (possibly <c>null</c>, meaning clear).</summary>
    /// <typeparam name="T">The field's value type.</typeparam>
    /// <param name="value">The value to store.</param>
    /// <returns>A set field.</returns>
    public static PatchField<T> Set<T>(T? value)
    {
        return new PatchField<T>(true, value);
    }
}
