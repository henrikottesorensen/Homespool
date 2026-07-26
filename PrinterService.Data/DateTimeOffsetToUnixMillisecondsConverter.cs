using System;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PrinterService.Data;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as milliseconds since the Unix epoch, in an INTEGER column.
/// </summary>
/// <remarks>
/// <para>
/// SQLite has no date/time type — only NULL, INTEGER, REAL, TEXT and BLOB. EF's default mapping for
/// <see cref="DateTimeOffset"/> is TEXT that includes the offset (<c>2026-07-22 21:38:21.844+00:00</c>),
/// which is not ordered lexicographically once offsets differ. The provider therefore <b>refuses to
/// translate comparisons at all</b>: any <c>Where</c> or <c>ExecuteDelete</c> on such a column throws
/// <c>InvalidOperationException: The LINQ expression … could not be translated</c> at runtime. That
/// would make the phase-4 retention sweep impossible, since a bulk server-side delete cannot be
/// filtered in memory.
/// </para>
/// <para>
/// Measured on 1M rows with an index on <c>(PrinterId, Timestamp)</c>: TEXT 81.4 MB, epoch
/// milliseconds ~39 MB — roughly <b>38 bytes per row</b>, because the value is stored twice, once in
/// the table and once in the index. Insert and sweep are also ~25-30% faster, though in absolute
/// terms that is a smaller consideration than the size.
/// </para>
/// <para>
/// <b>Why not <see cref="DateTimeOffsetToBinaryConverter"/>, EF's built-in?</b> It is <b>not
/// order-preserving across offsets</b>, and using it silently corrupts sorting and range filters:
/// </para>
/// <code>
/// v => ((v.Ticks / 1000) &lt;&lt; 11) | ((long)v.Offset.TotalMinutes &amp; 0x7FF)
/// </code>
/// <para>
/// Two faults compound. It uses <c>Ticks</c>, which is <i>local</i> and explicitly documented as "not
/// affected by the value of the Offset property", so one instant expressed in two offsets yields
/// different high bits. It then packs the offset into the low 11 bits, perturbing the ordering again.
/// The result is that rows at a positive offset sort to the wrong place and drop out of range
/// filters — with no error, which is the dangerous part. See
/// https://nitratine.net/blog/post/a-warning-for-ef-cores-datetimeoffsettobinaryconverter/.
/// </para>
/// <para>
/// <see cref="DateTimeOffset.ToUnixTimeMilliseconds"/> normalises to UTC <i>before</i> producing the
/// number, so a given instant always maps to the same value whatever offset it is expressed in. The
/// mapping is therefore strictly monotonic in the instant, which is exactly the property that makes
/// <c>&lt;</c> on the stored value mean the same thing as <c>&lt;</c> on the original. That is not a
/// detail — EF applies the converter to the parameter and compares stored representations, so a
/// non-monotonic converter produces wrong SQL rather than an error.
/// <c>DateTimeOffsetConverterTests</c> exists to hold that property down.
/// </para>
/// <para>
/// <b>Trade-offs, both acceptable here:</b> the original offset is discarded (values round-trip as
/// UTC), and precision is truncated to milliseconds. Every timestamp in this application originates
/// from <c>TimeProvider.System.GetUtcNow()</c>, so there is no offset to lose, and the fastest
/// telemetry cadence in the firmware is <c>TELEMETRY_INTERVAL_MIN</c> at 750 ms.
/// </para>
/// </remarks>
public class DateTimeOffsetToUnixMillisecondsConverter : ValueConverter<DateTimeOffset, long>
{
    /// <summary>Creates the converter with no mapping hints, which is how EF constructs it.</summary>
    /// <remarks>
    /// A real parameterless constructor is required, not merely an optional parameter: applying the
    /// converter by type — <c>HaveConversion&lt;T&gt;()</c> in <c>ConfigureConventions</c> — makes EF
    /// instantiate it reflectively, and optional parameters do not satisfy that.
    /// </remarks>
    public DateTimeOffsetToUnixMillisecondsConverter()
        : this(null)
    {
    }

    public DateTimeOffsetToUnixMillisecondsConverter(ConverterMappingHints? mappingHints)
        : base(v => v.ToUnixTimeMilliseconds(),
               v => DateTimeOffset.FromUnixTimeMilliseconds(v),
               mappingHints)
    {
    }

    public static ValueConverterInfo DefaultInfo { get; } =
        new(typeof(DateTimeOffset), typeof(long), i => new DateTimeOffsetToUnixMillisecondsConverter(i.MappingHints));
}
