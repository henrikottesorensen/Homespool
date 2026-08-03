using System;
using System.Globalization;
using System.Text;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// The name of a user's directory on disk: <c>{userId}-{displayName}</c>, e.g. <c>12-Sørensen</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The id is the identity and the name is decoration.</b> Lookup matches on the
/// <c>{userId}-</c> prefix alone and never reads what follows, which is what lets the suffix go stale
/// when somebody changes their display name, and what lets it be as Unicode as it likes. The whole
/// point is that a person poking around in the data directory can see whose files are whose.
/// </para>
/// <para>
/// <b>A denylist, not an allowlist</b> - unusual, and deliberate. An allowlist here would have to be
/// ASCII to be simple, and mangling <c>Sørensen</c> into <c>S-rensen</c> buys nothing: the
/// normalisation and case-folding hazards that argue for ASCII paths are all <i>lookup</i> hazards,
/// and nothing ever looks a directory up by this part. So the suffix has no correctness job, only a
/// legibility one, and what is excluded is the short list of things that would break a filesystem, a
/// shell, or a reader's eyes.
/// </para>
/// </remarks>
public static class UserDirectoryName
{
    /// <summary>
    /// Longest the decorative half may be, in <b>UTF-8 bytes</b> rather than characters - ext4's
    /// limit is 255 bytes per component, and 64 emoji are 256 bytes.
    /// </summary>
    public const int MaxSuffixBytes = 64;

    /// <summary>Names Windows refuses whatever the extension, all ASCII and all cheap to check.</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The directory name for a user, or a bare id when the display name survives sanitising as
    /// nothing at all.
    /// </summary>
    public static string For(long userId, string? displayName)
    {
        string id = userId.ToString(CultureInfo.InvariantCulture);
        string suffix = Sanitise(displayName);

        return suffix.Length == 0 ? id : $"{id}-{suffix}";
    }

    /// <summary>
    /// Reads the id back out of a directory name, or false if that name is not one of ours.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="For"/>, and it reads only the identifying half - everything from the
    /// first hyphen on is decoration and is not even looked at, which is the same rule
    /// <see cref="PatternFor"/> encodes for lookup. Anything else in the storage root - the incoming
    /// directory, or something a person put there - fails to parse and is thereby left alone.
    /// </remarks>
    public static bool TryParseUserId(string directoryName, out long userId)
    {
        ArgumentNullException.ThrowIfNull(directoryName);

        int hyphen = directoryName.IndexOf('-', StringComparison.Ordinal);
        ReadOnlySpan<char> identifying = hyphen < 0 ? directoryName : directoryName.AsSpan(0, hyphen);

        return long.TryParse(identifying, NumberStyles.None, CultureInfo.InvariantCulture, out userId);
    }

    /// <summary>The glob that finds a user's directory whatever its display half says.</summary>
    /// <remarks>
    /// Unambiguous across ids because the hyphen is required: <c>12-*</c> does not match
    /// <c>120-bob</c>. Only the <i>first</i> hyphen is significant, so a sanitised name containing
    /// one of its own is harmless.
    /// </remarks>
    public static string PatternFor(long userId)
    {
        return userId.ToString(CultureInfo.InvariantCulture) + "-*";
    }

    /// <summary>
    /// Reduces a display name to something safe to put in a path, keeping everything that is merely
    /// non-English.
    /// </summary>
    private static string Sanitise(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        // NFC so the same name typed on macOS and on Linux produces the same folder. Cosmetic, like
        // the rest of this, but it is what makes a listing look consistent across machines.
        StringBuilder builder = new();

        foreach (char character in displayName.Normalize(NormalizationForm.FormC))
        {
            builder.Append(IsUnsafe(character) ? '-' : character);
        }

        // Trailing dots and spaces are legal to create on Linux and silently trimmed by Windows,
        // which makes the same name two different directories depending on where it was made.
        string cleaned = builder.ToString().Trim().Trim('.', ' ');

        if (cleaned is "." or ".." || cleaned.Length == 0)
        {
            return string.Empty;
        }

        cleaned = TruncateToBytes(cleaned, MaxSuffixBytes);

        return Array.Exists(ReservedNames,
                            reserved => string.Equals(reserved, cleaned, StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : cleaned;
    }

    /// <summary>
    /// Path separators, control characters, and the bidi and zero-width marks that would let a
    /// display name render a directory listing deceptively - the one exclusion here that is about a
    /// reader rather than a filesystem.
    /// </summary>
    private static bool IsUnsafe(char character)
    {
        return character is '/' or '\\' or ':' or '\0'
        || char.IsControl(character)

        // Written as escapes on purpose: these are invisible characters, and a source file holding
        // them literally is unreadable in a diff and carries the very hazard this rejects.
        || character is '\u200B' or '\u200C' or '\u200D' or '\uFEFF'
        || character is >= '\u202A' and <= '\u202E'
        || character is >= '\u2066' and <= '\u2069';
    }

    /// <summary>Cuts to a byte budget without splitting a character in half.</summary>
    private static string TruncateToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        int taken = 0;

        for (int i = 0; i < value.Length;)
        {
            // Step by text element, so an astral character or a combining sequence is never halved.
            int width = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            int bytes = Encoding.UTF8.GetByteCount(value.AsSpan(i, width));

            if (taken + bytes > maxBytes)
            {
                return value[..i].TrimEnd();
            }

            taken += bytes;
            i += width;
        }

        return value;
    }
}
