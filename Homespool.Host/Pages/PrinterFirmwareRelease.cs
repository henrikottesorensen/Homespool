namespace Homespool.Host.Pages;

/// <summary>
/// The firmware a printer reported, as a page shows it at a glance.
/// </summary>
/// <remarks>
/// <b>Shared, for the same reason <see cref="PrinterDisplayName"/> is</b>: the front page's tile, the
/// listing's card and the printer's own status card all show it, and a reader comparing two of them
/// must not find one printer on two versions.
/// </remarks>
public static class PrinterFirmwareRelease
{
    /// <summary>
    /// The release part of what the printer stated - <c>6.4.0</c> for <c>6.4.0+11974</c> - or
    /// <see langword="null"/> when it has stated nothing yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cut at the <c>+</c> rather than parsed.</b> Build metadata identifies a build, not a release,
    /// and is noise at a glance; the page keeps the full string in a tooltip for the reader who needs
    /// it. Parsing would also drop a prerelease such as <c>7.0.0-RC1</c> as unreadable, and a
    /// prerelease is exactly the version somebody wants to see they are on.
    /// </para>
    /// <para>
    /// <b>A string with nothing before the <c>+</c> is shown whole</b>: it is the printer's own
    /// claim, and an empty label would read as no claim at all.
    /// </para>
    /// </remarks>
    public static string? For(string? stated)
    {
        if (string.IsNullOrWhiteSpace(stated))
        {
            return null;
        }

        string trimmed = stated.Trim();
        int plus = trimmed.IndexOf('+', System.StringComparison.Ordinal);
        string release = plus >= 0 ? trimmed[..plus].TrimEnd() : trimmed;

        return release.Length > 0 ? release : trimmed;
    }

    /// <summary>
    /// The whole stated version, for a tooltip, or <see langword="null"/> when <see cref="For"/>
    /// already shows all of it.
    /// </summary>
    /// <remarks>
    /// Null rather than a repeat, so Razor drops the attribute: a tooltip saying what the text beside
    /// it already says is a hover that answers nothing.
    /// </remarks>
    public static string? Tooltip(string? stated)
    {
        if (string.IsNullOrWhiteSpace(stated))
        {
            return null;
        }

        string whole = stated.Trim();

        return For(whole) == whole ? null : whole;
    }
}
