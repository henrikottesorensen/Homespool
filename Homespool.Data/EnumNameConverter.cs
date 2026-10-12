using System;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Homespool.Model;

namespace Homespool.Data;

/// <summary>
/// Stores an enum as its member's name, and reads back only a name a member declares, exactly as
/// declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>HasConversion&lt;string&gt;()</c>:</b> EF's own string converter reads with
/// <c>Enum.TryParse</c>'s leniency. Over SQLite it reads <c>printing</c> as <c>Printing</c>, <c>2</c> as
/// the member numbered 2, <c>99</c> as a value no member carries, <c>Idle,Busy</c> as the two ORed -
/// another member, since every enum here is numbered 1, 2, 3… - a name with a space either side as the
/// name, and an empty column as <c>Undefined</c>. Only an unknown name throws. This reads through
/// <see cref="EnumValues.TryParseName{TEnum}"/>, the same rule every hand-written reader here follows, so
/// a column says one thing whoever reads it.
/// </para>
/// <para>
/// <b>Writing refuses a value no member carries</b>, where EF's converter stores its number as text. The
/// reader above would refuse that row, so accepting the write would only move the failure from the
/// code that made the mistake to whoever reads the row next. A query comparing a column against such a
/// value throws for the same reason, rather than matching nothing.
/// </para>
/// <para>
/// <c>Undefined</c> reads and writes like any other member. Whether a column may hold it is a question
/// about that column, which a converter shared by all of them cannot answer.
/// </para>
/// <para>
/// <b>One thing EF does only for its own converter:</b> it translates <c>column.ToString()</c> inside a
/// query to the stored text. Over a column using this one, that query fails to translate. Compare the
/// column with a member instead, which is what the stored text is for.
/// </para>
/// </remarks>
/// <typeparam name="TEnum">The enum the column holds.</typeparam>
public sealed class EnumNameConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    /// <summary>Creates the converter, which is how EF constructs it from <c>HasConversion&lt;T&gt;()</c>.</summary>
    public EnumNameConverter()
        : base(value => NameOf(value), name => MemberNamed(name))
    {
    }

    private static string NameOf(TEnum value)
    {
        return Enum.GetName(value) ??
            throw new InvalidOperationException(
                $"{typeof(TEnum).Name} {value} is no member, so it has no name to store.");
    }

    private static TEnum MemberNamed(string name)
    {
        return EnumValues.TryParseName(name, out TEnum value) ?
            value :
            throw new InvalidOperationException(
                $"'{name}' in the database is not the name of a {typeof(TEnum).Name} member. A member renamed or " +
                "removed without migrating the rows that hold it reads this way.");
    }
}
