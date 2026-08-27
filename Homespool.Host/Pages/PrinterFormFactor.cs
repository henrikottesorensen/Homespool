namespace Homespool.Host.Pages;

/// <summary>
/// Which of the two printer drawings a machine gets on the front page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Form factor, and deliberately nothing finer.</b> The front page draws a printer, not
/// <i>this</i> printer: an open frame or a closed box. Both are generic engineering shapes that
/// dozens of manufacturers build, which is what keeps the drawing ours - the same reasoning that
/// chose the house blue as "not Prusa's orange".
/// A recognisable per-model likeness would be somebody else's trade dress with our
/// stroke width on it.
/// </para>
/// <para>
/// <b>Not a table keyed on <see cref="Model.PrinterModelGroup"/>, and that is the whole point.</b>
/// Writing <c>CoreOne =&gt; Enclosed</c> beside <c>Mk4 =&gt; Open</c> reads as obvious, and every row
/// of it is a claim about hardware that nothing in this codebase can check - written from memory of a
/// product line, which is exactly how three wrong hardware claims got written down in a single day.
/// What the printer reports is checkable, so this asks the printer
/// instead and the question never has to be answered from memory again.
/// </para>
/// </remarks>
public enum PrinterFormFactor
{
    /// <summary>Not worked out yet. Never rendered - <see cref="PrinterFormFactors.For"/> never returns it.</summary>
    Undefined = 0,

    /// <summary>An open frame. The default, and what a printer that has told us nothing gets.</summary>
    Open = 1,

    /// <summary>A closed box, because the printer reported a chamber or an enclosure.</summary>
    Enclosed = 2,
}
