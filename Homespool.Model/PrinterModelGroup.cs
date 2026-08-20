namespace Homespool.Model;

/// <summary>
/// A family of printers that print each other's G-code interchangeably - firmware's
/// <c>PrinterModelCompatibilityGroup</c>, mirrored.
/// </summary>
/// <remarks>
/// <b>Not one value per model.</b> An MK3 and an MK3S share a group, as do an MK3.9 and an MK4,
/// because the question this answers is "will this file drive this machine correctly", and on that
/// question they are the same printer. See <see cref="PrinterModelCompatibility"/> for the upgrade
/// path between groups, which is the part that carries the direction.
/// </remarks>
public enum PrinterModelGroup
{
    /// <summary>
    /// Not a group: a model neither firmware's table nor this one knows. Yields no claim rather
    /// than a refusal - see <see cref="PrinterModelCompatibility"/>.
    /// </summary>
    Unknown = 0,

    Mk3,

    Mk3_5,

    Mk4,

    Mk4S,

    Mini,

    Xl,

    /// <summary>The XL+, which prints XL files but not the other way round. New in firmware 6.8.</summary>
    Xlp,

    Ix,

    CoreOne,

    CoreOneL,

    CoreOneIndx,

    CoreOneLIndx,
}
