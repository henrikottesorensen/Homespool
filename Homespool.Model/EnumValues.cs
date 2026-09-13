using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Homespool.Model;

/// <summary>
/// Whether an enum value is one somebody actually set: a named member, and not the reserved
/// <c>Undefined</c> at zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not called <c>IsDefined</c></b>, because <c>Undefined</c> is a defined member in the framework's
/// sense - <see cref="Enum.IsDefined{TEnum}(TEnum)"/> answers true for it - and a method of that name
/// answering false would contradict itself at every call site. The question asked here is the other one,
/// whether anybody assigned it.
/// </para>
/// <para>
/// <b>It can name no member because every enum puts <c>Undefined</c> at zero</b>, which
/// <c>EnumConventionTests</c> enforces; so "not the default" and "not <c>Undefined</c>" are one test.
/// An enum that broke the convention would fail that test before it could mislead this one.
/// </para>
/// <para>
/// <b>For guards, not for data.</b> Where <c>Undefined</c> is a state code must handle - a value that
/// was genuinely never recorded - handle it by name rather than rejecting it here. And not for a
/// <see cref="FlagsAttribute"/> enum, whose combinations are not single members; none exists today.
/// </para>
/// </remarks>
public static class EnumValues
{
    /// <summary>
    /// Whether <paramref name="value"/> is a named member other than <c>Undefined</c>: false for the
    /// default, and false for a number no member carries.
    /// </summary>
    /// <typeparam name="TEnum">An enum that reserves <c>Undefined</c> at zero, as every enum here does.</typeparam>
    public static bool IsSet<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        return !EqualityComparer<TEnum>.Default.Equals(value, default) && Enum.IsDefined(value);
    }

    /// <summary>
    /// <paramref name="value"/> itself when <see cref="IsSet{TEnum}"/>, and otherwise a throw naming the
    /// argument - for a guard clause, where an unset value is a programming error rather than an answer.
    /// </summary>
    /// <typeparam name="TEnum">An enum that reserves <c>Undefined</c> at zero, as every enum here does.</typeparam>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is <c>Undefined</c> or no member at all.</exception>
    public static TEnum RequireSet<TEnum>(this TEnum value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where TEnum : struct, Enum
    {
        return value.IsSet()
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, $"{typeof(TEnum).Name} has to be set to one of its members.");
    }
}
