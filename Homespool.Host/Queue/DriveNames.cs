using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Homespool.Host.PrintFiles;
using Homespool.Host.Services;

namespace Homespool.Host.Queue;

/// <summary>
/// The names a file may take on a printer's drive, in the order they are tried: its own, then with
/// its owner's name added - <c>part.gcode</c>, <c>part (bob).gcode</c>, <c>part (bob 2).gcode</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file needs more than one.</b> A name is unique per user in the store, but every user's
/// files land in the one <c>/usb/</c> of a printer they share, and firmware refuses a transfer to a
/// name already there. Adopting what is there by its size would print one person's bytes for
/// another; holding until somebody clears the drive stops a queue over a coincidence. So the second
/// file of a name goes on under its owner's, which also tells whoever stands at the panel whose it
/// is.
/// </para>
/// <para>
/// <b>Every name fits <see cref="UserFileStore.MaxNameLength"/></b>, the printer's limit: the stem
/// gives way, never the extension or the owner, since a name cut into its suffix would say nothing
/// and one cut into its extension would not print.
/// </para>
/// </remarks>
public static class DriveNames
{
    /// <summary>How many numbered names are tried after the owner's own before giving up.</summary>
    public const int MaxNumbered = 99;

    /// <summary>The longest owner's name put in a suffix, in UTF-16 units.</summary>
    /// <remarks>Leaves most of the printer's 167 to the file's own name, which is what people read.</remarks>
    public const int MaxOwnerLength = 32;

    /// <summary>Characters the printer's FAT refuses in a name, and the path separators.</summary>
    private const string Refused = "\"*/:<>?\\|";

    /// <summary>
    /// The first name for <paramref name="fileName"/> that <paramref name="isTaken"/> does not claim,
    /// or null when every one is.
    /// </summary>
    public static string? First(string fileName, string? ownerName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);

        return Candidates(fileName, ownerName).FirstOrDefault(candidate => !isTaken(candidate));
    }

    /// <summary>
    /// The first name after <paramref name="current"/> that <paramref name="isTaken"/> does not claim,
    /// or null when none is left - what a refusal of <paramref name="current"/> moves on to.
    /// </summary>
    public static string? After(string current, string fileName, string? ownerName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);

        return Candidates(fileName, ownerName).SkipWhile(candidate => !Same(candidate, current))
                                              .Skip(1)
                                              .FirstOrDefault(candidate => !isTaken(candidate));
    }

    /// <summary>Whether two names are one on a printer's drive, which folds case.</summary>
    public static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every name tried for <paramref name="fileName"/>, in order.</summary>
    public static IEnumerable<string> Candidates(string fileName, string? ownerName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        yield return fileName;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string owner = Clean(ownerName);

        for (int n = 1; n <= MaxNumbered; n++)
        {
            // Without a usable owner's name the suffix is a number alone, counted from 2 so that the
            // first copy reads as the second file of the name, which is what it is.
            string mark = owner.Length == 0 ?
                (n + 1).ToString(CultureInfo.InvariantCulture) :
                n == 1 ? owner : $"{owner} {n.ToString(CultureInfo.InvariantCulture)}";

            yield return Fit(stem, $" ({mark})", extension);
        }
    }

    /// <summary>The stem shortened until stem, suffix and extension fit the printer's limit.</summary>
    private static string Fit(string stem, string suffix, string extension)
    {
        int room = UserFileStore.MaxNameLength - suffix.Length - extension.Length;

        return stem.Length <= room ? stem + suffix + extension : Cut(stem, Math.Max(room, 0)) + suffix + extension;
    }

    /// <summary>
    /// An owner's name made safe for the drive: nothing invisible, nothing the FAT refuses, and short
    /// enough to leave the file's own name room.
    /// </summary>
    /// <remarks>
    /// A username already admits none of these, but the guarantee belongs to the name on the drive,
    /// not to whatever the account rules are today.
    /// </remarks>
    private static string Clean(string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            return string.Empty;
        }

        char[] characters = PrintableText.Replace(ownerName, '-').ToCharArray();

        for (int i = 0; i < characters.Length; i++)
        {
            if (Refused.Contains(characters[i], StringComparison.Ordinal))
            {
                characters[i] = '-';
            }
        }

        return Cut(new string(characters).Trim(), MaxOwnerLength);
    }

    /// <summary>At most <paramref name="length"/> units of <paramref name="text"/>, never half a surrogate pair.</summary>
    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        int end = length > 0 && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

        return text[..end];
    }
}
