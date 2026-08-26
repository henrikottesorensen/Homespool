using System;

using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// How long is left on a print, said to the precision the printer actually reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seconds were ours, not the printer's.</b> Firmware reports the estimate in whole minutes -
/// measured rather than assumed: of 27 026 telemetry samples carrying one on the appliance, every
/// single one was an exact multiple of sixty. Rendering it as <c>0:04:00</c> put two digits on the
/// end that could only ever be zero, which reads as a countdown accurate to the second and is not.
/// </para>
/// <para>
/// <b>Elapsed keeps its seconds</b>, and that asymmetry is the point rather than an oversight. The
/// same query shows <c>TimePrinting</c> spread across every residue, so those seconds are real.
/// </para>
/// <para>
/// <b>Units rather than a clock face.</b> <c>0:04</c> is four minutes and reads as four seconds -
/// the very confusion dropping the seconds was meant to end. Spelling them also sidesteps the
/// separator problem underneath: a custom <see cref="TimeSpan"/> format escapes <c>:</c> rather than
/// looking it up, and the <c>g</c> specifier that does respect the culture always prints seconds.
/// </para>
/// <para>
/// <b>Spelled out rather than abbreviated</b>, measured rather than assumed: <c>2 hours 23
/// minutes</c> beside the percentage fits an 11rem tile on a phone without wrapping, which was the
/// only thing <c>h</c>/<c>min</c> was buying. Spelling them brings the plurals with it, which the
/// abbreviation had been hiding - hence the parts and a joiner rather than one string, since
/// <c>1 hour 23 minutes</c> inflects each half independently.
/// </para>
/// <para>
/// <b>A static taking the localiser</b>, like <see cref="Localisation.Plural.Format"/> - same shape,
/// same reason: a view already has one, and the words belong in the resources rather than here.
/// </para>
/// </remarks>
public static class PrintDuration
{
    /// <summary>
    /// Hours and minutes, with no seconds. Hours are not wrapped at 24 - a two-day print says 50.
    /// </summary>
    /// <remarks>
    /// <b>Each part is dropped when it is zero</b>, which is how a person says it: <c>4 minutes</c>
    /// rather than <c>0 hours 4 minutes</c>, and <c>2 hours</c> rather than <c>2 hours 0 minutes</c>.
    /// </remarks>
    public static string WithoutSeconds(IStringLocalizer localiser, int seconds)
    {
        ArgumentNullException.ThrowIfNull(localiser);

        TimeSpan span = TimeSpan.FromSeconds(seconds);

        // TotalHours rather than Hours, so a print longer than a day does not silently start again
        // from zero - the estimate is what somebody plans an evening around.
        int hours = (int)span.TotalHours;

        if (hours == 0)
        {
            return Plural.Format(localiser, "Common_Minutes", span.Minutes);
        }

        string spelledHours = Plural.Format(localiser, "Common_Hours", hours);

        if (span.Minutes == 0)
        {
            return spelledHours;
        }

        // Joined through a resource rather than with a space, so a language that wants a word between
        // the two halves has somewhere to put it.
        return localiser[
            "Common_DurationParts",
            spelledHours,
            Plural.Format(localiser, "Common_Minutes", span.Minutes)].Value;
    }
}
