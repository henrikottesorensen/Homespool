using Homespool.Model.Entities;

namespace Homespool.Host.Pages;

/// <summary>
/// Works out which drawing a printer gets, from what the printer itself has reported.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evidence rather than a lookup.</b> See <see cref="PrinterFormFactor"/> for why a table keyed on
/// the model group is the wrong shape - briefly, every row of one would be an unverifiable claim about
/// hardware, and what the printer reports is checkable.
/// </para>
/// <para>
/// <b>It reads <see cref="PrinterLiveState"/>, which is persisted</b>, so a printer switched off at
/// the wall keeps the drawing it earned while it was awake. Deriving this from telemetry <i>rows</i>
/// instead would make the front page change its mind about a machine every time history aged out.
/// </para>
/// </remarks>
public static class PrinterFormFactors
{
    /// <summary>
    /// The drawing for a printer, defaulting to <see cref="PrinterFormFactor.Open"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Temperatures only, and the fans are left out on purpose.</b> A chamber or enclosure
    /// temperature means a sensor sitting in one, which is as close to proof as the wire carries. The
    /// chamber <em>fan</em> fields are not used as evidence because nothing here has established that
    /// an open-framed printer leaves them null - and guessing that it does is the same class of
    /// unchecked hardware claim this whole type exists to avoid.
    /// </para>
    /// <para>
    /// <b>Open is the default, so an unknown printer is drawn as the commoner thing</b> rather than
    /// given a third "we do not know" drawing. The plaque underneath carries the printer's name and
    /// status, which is the part a person is actually reading; the silhouette is orientation, not a
    /// specification.
    /// </para>
    /// </remarks>
    public static PrinterFormFactor For(PrinterLiveState? live)
    {
        if (live is null)
        {
            return PrinterFormFactor.Open;
        }

        bool enclosed = live.ChamberTemperature.HasValue || live.EnclosureTemperature.HasValue;

        return enclosed ? PrinterFormFactor.Enclosed : PrinterFormFactor.Open;
    }
}
