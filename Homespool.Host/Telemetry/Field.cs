using System.Diagnostics.CodeAnalysis;

namespace Homespool.Host.Telemetry;

/// <summary>
/// One cell of a <see cref="TelemetryUpdate"/>: what a message said about a single field, in the
/// three states every surveyed protocol needs and no protocol needs more of - <i>nothing</i>
/// (absent, keep last-known), <i>a value</i>, or an authoritative <i>gone</i> (present null).
/// JSON Merge Patch semantics (RFC 7386), typed.
/// </summary>
/// <typeparam name="T">The field's value type, nullable where the column is.</typeparam>
/// <remarks>
/// <para>
/// A plain nullable can express only two of the three states - null has to mean either "unchanged"
/// or "gone", and choosing wrong is how a finished print reported 99% for nine hours.
/// The third state is the whole reason this type exists, and the
/// default is <see cref="Absent"/> so an edge mapper that forgets a field says nothing about it
/// rather than destroying it.
/// </para>
/// <para>
/// The implicit conversion maps null to <see cref="Absent"/> - the coalesce idiom, so a mapper
/// writes <c>Progress = dto.Progress</c> for "present only when the wire sent it". A deliberate
/// clear is <see cref="Null"/>; an authoritative write that may itself be null is
/// <see cref="Of"/>. The three spellings are the three merge policies.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
                 Justification = "Absent/Null/Of are the type's whole API, and the explicit type "
                                 + "argument is the point: Field<int?>.Null names exactly which "
                                 + "cell is being cleared. A non-generic factory would infer "
                                 + "Field<int> from an int? argument and silently mistype the cell.")]
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
                 Justification = "Of is the named alternate; it differs deliberately (always "
                                 + "present) because the implicit conversion's null-to-Absent is "
                                 + "the coalesce idiom, not a general conversion.")]
public readonly record struct Field<T>
{
    private Field(bool isPresent, T? value)
    {
        IsPresent = isPresent;
        Value = value;
    }

    /// <summary>Whether the message said anything about this field at all.</summary>
    public bool IsPresent { get; }

    /// <summary>The said value - meaningful only when <see cref="IsPresent"/>, and null there
    /// means authoritatively empty, not unknown.</summary>
    public T? Value { get; }

    /// <summary>The message said nothing: keep last-known. The <c>default</c> of this type.</summary>
    public static Field<T> Absent => default;

    /// <summary>The message said this field is gone - present, value null.</summary>
    public static Field<T> Null => new(true, default);

    /// <summary>An authoritative statement of <paramref name="value"/>, null included - the
    /// full-push and atomic-block idiom, where absence of a value still means something.</summary>
    public static Field<T> Of(T? value)
    {
        return new Field<T>(true, value);
    }

    /// <summary>Null becomes <see cref="Absent"/>, a value becomes present - the coalesce idiom.</summary>
    public static implicit operator Field<T>(T? value)
    {
        return value is null ? default : new Field<T>(true, value);
    }
}
