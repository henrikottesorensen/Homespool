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

               // Half a character. Whoever asks about a char at a time has not paired it, and the safe
               // answer to that is no - IsUnprintable(Rune) is how a whole one outside the basic plane
               // is judged.
               char.IsSurrogate(character) ||

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

    /// <summary>
    /// <see cref="IsUnprintable(char)"/> for a whole character, which is the only way to ask about
    /// one outside the basic plane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Out there the same last group applies - no width, or no ink - and it is listed the same way:
    /// the tag block, whose characters mirror ASCII invisibly and so carry text nobody can see; the
    /// Egyptian hieroglyph, shorthand and musical format controls.
    /// </para>
    /// <para>
    /// <b>Still not the <c>Format</c> category</b>, for the reason the basic plane gives: the Kaithi
    /// number signs are <c>Format</c> characters and are visible. <b>And not the variation
    /// selectors</b>, invisible as they are - one follows nearly every emoji, and refusing it would
    /// refuse the emoji.
    /// </para>
    /// </remarks>
    public static bool IsUnprintable(Rune rune)
    {
        if (rune.IsBmp)
        {
            return IsUnprintable((char)rune.Value);
        }

        return rune.Value is >= 0xE0000 and <= 0xE007F ||
               rune.Value is >= 0x13430 and <= 0x1343F ||
               rune.Value is >= 0x1BCA0 and <= 0x1BCA3 ||
               rune.Value is >= 0x1D173 and <= 0x1D17A;
    }

    /// <summary>
    /// Whether every character of <paramref name="value"/> is one <see cref="IsUnprintable(Rune)"/>
    /// accepts - and is a character: a surrogate with no partner is not.
    /// </summary>
    public static bool IsPrintable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return FirstUnprintable(value) < 0;
    }

    /// <summary>
    /// <paramref name="value"/> with each unprintable character replaced by <c>U+FFFD</c>, or the
    /// same string back when there is nothing to replace. Nothing at all is the empty string.
    /// </summary>
    public static string Replace(string? value)
    {
        return Replace(value, Replacement);
    }

    /// <summary>
    /// As <see cref="Replace(string)"/>, with <paramref name="replacement"/> in place of <c>U+FFFD</c>
    /// - for somewhere that has its own, as a directory name has its hyphen.
    /// </summary>
    /// <remarks>
    /// One replacement for one character, whether it took one <see cref="char"/> or two, and one for
    /// a surrogate with no partner.
    /// </remarks>
    public static string Replace(string? value, char replacement)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int index = FirstUnprintable(value);

        // Built only once something has to change, so the ordinary case - every real printer, every
        // request - allocates nothing and returns the caller's own string.
        if (index < 0)
        {
            return value;
        }

        StringBuilder replaced = new StringBuilder(value.Length).Append(value, 0, index);

        while (index < value.Length)
        {
            int length = LengthAt(value, index, out bool unprintable);

            if (unprintable)
            {
                replaced.Append(replacement);
            }
            else
            {
                replaced.Append(value, index, length);
            }

            index += length;
        }

        return replaced.ToString();
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

    /// <summary>Where the first unprintable character starts, or -1 when there is none.</summary>
    private static int FirstUnprintable(string value)
    {
        for (int index = 0; index < value.Length;)
        {
            int length = LengthAt(value, index, out bool unprintable);

            if (unprintable)
            {
                return index;
            }

            index += length;
        }

        return -1;
    }

    /// <summary>
    /// How many <see cref="char"/>s the character at <paramref name="index"/> takes, and whether it
    /// is unprintable - which a surrogate with no partner always is.
    /// </summary>
    private static int LengthAt(string value, int index, out bool unprintable)
    {
        if (!Rune.TryGetRuneAt(value, index, out Rune rune))
        {
            unprintable = true;

            return 1;
        }

        unprintable = IsUnprintable(rune);

        return rune.Utf16SequenceLength;
    }
}
