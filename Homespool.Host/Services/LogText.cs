using System;
using System.Text;

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
/// The excluded set is the one <c>UserDirectoryName</c> and <c>Passkeys.IsAcceptableName</c> already
/// refuse, minus the path separators those two add for reasons that are about a filesystem rather
/// than a reader.
/// </para>
/// </remarks>
public static class LogText
{
    private const char Replacement = '\uFFFD';

    /// <summary>
    /// <paramref name="value"/> with anything unprintable replaced, or the same string back when it
    /// carries nothing to replace.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Built only once something has to change, so the ordinary case - every real printer, every
        // request - allocates nothing and returns the caller's own string.
        StringBuilder? cleaned = null;

        for (int index = 0; index < value.Length; index++)
        {
            if (!IsUnprintable(value[index]))
            {
                cleaned?.Append(value[index]);

                continue;
            }

            cleaned ??= new StringBuilder(value.Length).Append(value, 0, index);
            cleaned.Append(Replacement);
        }

        return cleaned?.ToString() ?? value;
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
    /// </remarks>
    public static string Clean(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return Clean(value);
        }

        // Never mid-pair: a lone surrogate is not a character, and it reaches whatever writes the log
        // as ill-formed UTF-16 - which some JSON writers refuse outright. Cutting one earlier costs a
        // character of an already-truncated name.
        int cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return Clean(value[..cut]) + $"<{value.Length} characters in all>";
    }

    /// <summary>
    /// Control characters, and the bidi and zero-width marks that would let a value render a log line
    /// deceptively without carrying a control character at all.
    /// </summary>
    private static bool IsUnprintable(char character)
    {
        return char.IsControl(character)

               // Written as escapes on purpose: these are invisible characters, and a source file holding
               // them literally is unreadable in a diff and carries the very hazard this rejects.
               || character is '\u200B' or '\u200C' or '\u200D' or '\uFEFF'
               || character is >= '\u202A' and <= '\u202E'
               || character is >= '\u2066' and <= '\u2069';
    }
}
