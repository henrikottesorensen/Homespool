using System;
using System.Text;

namespace Homespool.Host.Services;

/// <summary>
/// Text in which what a person reads is what was stored: no control characters, and none of the
/// invisible marks that reorder or hide the characters around them.
/// </summary>
/// <remarks>
/// <para>
/// The one definition of that set, for every place a stranger's string lands - a log line, a file
/// name, a claim off a provider's ticket. The places differ in what they do about a character that
/// fails it: a name that will be looked up again is refused, because replacing a character could
/// make two names one; a value that is only ever shown is replaced, because refusing a sign-in over
/// somebody's display name helps nobody. This type answers the question and does both jobs; which
/// one applies is the caller's decision.
/// </para>
/// <para>
/// <b>Replaced, never dropped.</b> <c>MK4</c> and a newline, shown as <c>MK4</c>, reads as a clean
/// value nobody sent. <c>U+FFFD</c> keeps the length and says something was there.
/// </para>
/// <para>
/// Everything merely non-English passes. The set is about rendering, not alphabet.
/// </para>
/// </remarks>
public static class PrintableText
{
    private const char Replacement = '\uFFFD';

    /// <summary>
    /// Control characters, and the characters that let a value render as something other than what
    /// it holds without carrying a control character at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four groups, each listed whole rather than by the members somebody happened to think of:
    /// the controls, which is <see cref="char.IsControl(char)"/> - C0, <c>DEL</c> and C1; the line
    /// and paragraph separators, which break a line as surely as a newline and are not controls;
    /// every character Unicode gives the <c>Bidi_Control</c> property; and the ones with no width or
    /// no ink - the soft hyphen, the Mongolian vowel separator, the zero-width space and joiners,
    /// the word joiner and the invisible operators after it, and the byte-order mark.
    /// </para>
    /// <para>
    /// <b>Listed, not taken by category.</b> The <c>Format</c> category would be shorter to write
    /// and also holds the Arabic number signs, which are visible and belong in somebody's file name.
    /// A log line can afford that and <see cref="PrintableLogEnricher"/> takes the category; a rule
    /// that refuses names cannot.
    /// </para>
    /// </remarks>
    public static bool IsUnprintable(char character)
    {
        // Written as escapes on purpose: these are invisible characters, and a source file holding
        // them literally is unreadable in a diff and carries the very hazard this rejects.
        return char.IsControl(character) ||

               // Line and paragraph separator.
               character is '\u2028' or '\u2029' ||

               // Bidi_Control: the Arabic letter mark, the two directional marks, the embeddings and
               // overrides, the isolates.
               character is '\u061C' or '\u200E' or '\u200F' ||
               character is >= '\u202A' and <= '\u202E' ||
               character is >= '\u2066' and <= '\u2069' ||

               // No width, or no ink.
               character is '\u00AD' or '\u180E' or '\uFEFF' ||
               character is >= '\u200B' and <= '\u200D' ||
               character is >= '\u2060' and <= '\u2064';
    }

    /// <summary>Whether <paramref name="value"/> holds nothing <see cref="IsUnprintable"/> refuses.</summary>
    public static bool IsPrintable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (char character in value)
        {
            if (IsUnprintable(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="value"/> with each unprintable character replaced by <c>U+FFFD</c>, or the
    /// same string back when there is nothing to replace. Nothing at all is the empty string.
    /// </summary>
    public static string Replace(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Built only once something has to change, so the ordinary case - every real printer, every
        // request - allocates nothing and returns the caller's own string.
        StringBuilder? replaced = null;

        for (int index = 0; index < value.Length; index++)
        {
            if (!IsUnprintable(value[index]))
            {
                replaced?.Append(value[index]);

                continue;
            }

            replaced ??= new StringBuilder(value.Length).Append(value, 0, index);
            replaced.Append(Replacement);
        }

        return replaced?.ToString() ?? value;
    }

    /// <summary>
    /// As <see cref="Replace(string)"/>, and cut to at most <paramref name="maxLength"/> UTF-16 code
    /// units - what <see cref="string.Length"/> counts - with nothing added to say so.
    /// </summary>
    /// <remarks>
    /// For a value that goes on being used as itself: a marker belongs in a log line, and would become
    /// part of a display name. The cut never falls between the halves of a surrogate pair, which would
    /// leave half a character - ill-formed UTF-16 that some writers refuse outright.
    /// </remarks>
    public static string Replace(string? value, int maxLength)
    {
        return Replace(Cut(value, maxLength));
    }

    /// <summary>
    /// The first <paramref name="maxLength"/> UTF-16 code units of <paramref name="value"/>, one fewer
    /// where that would split a surrogate pair, or the same string back when it is short enough.
    /// </summary>
    public static string? Cut(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }
}
