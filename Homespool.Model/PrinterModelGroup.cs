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
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    /// <remarks>
    /// <b><see cref="Unknown"/> is not this</b>: it is the answer you get having looked at a model
    /// name and not recognised it, which is a fact about the printer. This is the absence of anybody
    /// having looked.
    /// </remarks>
    Undefined = 0,

    /// <summary>
    /// Not a group: a model neither firmware's table nor this one knows. Yields no claim rather
    /// than a refusal - see <see cref="PrinterModelCompatibility"/>.
    /// </summary>
    Unknown = 1,

    Mk3 = 2,

    Mk3_5 = 3,

    Mk4 = 4,

    Mk4S = 5,

    Mini = 6,

    Xl = 7,

    /// <summary>The XL+, which prints XL files but not the other way round. New in firmware 6.8.</summary>
    Xlp = 8,

    Ix = 9,

    CoreOne = 10,

    CoreOneL = 11,

    CoreOneIndx = 12,

    CoreOneLIndx = 13,
}
