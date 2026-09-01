using System;
using System.Globalization;

using Microsoft.Extensions.Localization;

namespace Homespool.Host.Localisation;

/// <summary>
/// A byte count written the way the reader writes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own class rather than a helper on the files page.</b> Three pages reached across for it
/// there - the dashboard, the settings page and the files page itself - which is the point at which
/// a formatter has stopped belonging to any one of them.
/// </para>
/// <para>
/// <b>IEC prefixes, because the arithmetic is binary</b> (Henrik, 2026-09-01). This divides by 1024
/// and then called the result <c>MB</c>, which names a different number - a megabyte is 1000000
/// bytes and this was never showing one. <c>MiB</c> is what the division actually produces, so the
/// label now matches the sum.
/// </para>
/// <para>
/// <b>The unit is translated, not only the number</b> (Henrik, 2026-09-01). This localised the
/// separator and then hardcoded the suffix beside it, which is half a job: French writes
/// <c>Mio</c> for this quantity, so a French reader would have had their own decimal comma next to
/// somebody else's unit. No shipped language is wrong today - English and Danish both write
/// <c>MiB</c> - so this is paid before the first language that would be, rather than after.
/// </para>
/// <para>
/// <b>The keys are written out rather than composed from the unit</b>, so a search for one finds it
/// and the test that hunts orphaned resources can see them.
/// </para>
/// </remarks>
public static class ByteSize
{
    private static readonly string[] UnitKeys = ["ByteUnit_B", "ByteUnit_KiB", "ByteUnit_MiB", "ByteUnit_GiB"];

    /// <summary>
    /// Formats a byte count for a person.
    /// </summary>
    /// <param name="bytes">The count.</param>
    /// <param name="localiser">Supplies the unit in the reader's language.</param>
    /// <returns>Something like <c>512 MiB</c>, or <c>4,1 MiB</c> to a Danish reader.</returns>
    public static string Format(long bytes, IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(localiser);

        // Binary units, because that is what a printer's storage uses.
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < UnitKeys.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        string suffix = localiser[UnitKeys[unit]];

        // No decimal on bytes, one everywhere else: "512 B" and "4.1 MiB" both read better than the
        // alternative.
        //
        // The reader's culture, not the invariant one. This was invariant "so the separator does not
        // move with the server's locale", which was the right instinct answered on the wrong axis:
        // the danger was never that a Danish *server* would render 4,1 MiB to an English reader, it
        // was that the separator would follow the machine rather than the person. Now that a request
        // carries the reader's culture, following it is what puts the separator where they read it -
        // and 4,1 MiB is simply how a number is written in Danish, so invariant here means being
        // wrong for them on purpose. The precision hazard on this value is a separate question
        // from the culture one.
        return unit == 0 ?
            string.Create(CultureInfo.CurrentCulture, $"{bytes} {suffix}") :
            string.Create(CultureInfo.CurrentCulture, $"{size:0.#} {suffix}");
    }
}
