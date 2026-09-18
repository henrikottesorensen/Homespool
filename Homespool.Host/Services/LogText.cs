using System;

namespace Homespool.Host.Services;

/// <summary>
/// Makes a caller-supplied string safe to put in a log line: control characters and the invisible
/// marks that reorder or hide text are replaced with <c>U+FFFD</c>, everything merely non-English is
/// kept.
/// </summary>
/// <remarks>
/// <para>
/// For values that arrive off the wire or out of a form, where the sender chooses every byte. A
/// newline forges a record in any line-oriented log, and an escape sequence repaints the terminal of
/// whoever reads one - neither needs the value itself to be interesting. JSON escapes both, so a
/// formatter is a defence as long as it stays one; this makes the log site safe whatever it writes
/// into.
/// </para>
/// <para>
/// <b>Replaced rather than dropped.</b> A serial of <c>MK4\n</c> logged as <c>MK4</c> reads as a
/// clean value that was never sent, which is worse than an ugly one: it hides that somebody put a
/// newline in a serial number. The replacement character keeps the length and says so.
/// </para>
/// <para>
/// What counts as unprintable is <see cref="PrintableText"/>'s to say, and this is its use for a log
/// line: replace, and where a value has no length of its own, cut it and say how much arrived.
/// </para>
/// </remarks>
public static class LogText
{
    /// <summary>
    /// <paramref name="value"/> with anything unprintable replaced, or the same string back when it
    /// carries nothing to replace.
    /// </summary>
    public static string Clean(string? value)
    {
        return PrintableText.Replace(value);
    }

    /// <summary>
    /// As <see cref="Clean(string)"/>, and cut to <paramref name="maxLength"/> characters with a
    /// marker naming the length that arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the log sites whose value has no length of its own - a JSON property name off the wire,
    /// where the sender picks the size as well as the bytes. Cleaning bounds what a value can do;
    /// this bounds what it can cost.
    /// </para>
    /// <para>
    /// <b>The kept prefix is the point</b>, which is where this differs from
    /// <c>PrinterTrafficLog</c>'s elision: that drops an over-long <i>value</i> whole, because a
    /// thumbnail explains nothing about a misbehaving printer. A name is an identifier, and an
    /// operator reading the line needs to see what it started with.
    /// </para>
    /// <para>
    /// <b>The marker is what makes this the log's own</b>: a reader of a line wants to know the value
    /// was absurd, where anything that keeps the value wants <see cref="PrintableText.Replace(string, int)"/>,
    /// which cuts and says nothing.
    /// </para>
    /// </remarks>
    public static string Clean(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return Clean(value);
        }

        return PrintableText.Replace(value, maxLength) + $"<{value.Length} characters in all>";
    }
}
