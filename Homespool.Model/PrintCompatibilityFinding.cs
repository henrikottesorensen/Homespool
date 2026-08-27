namespace Homespool.Model;

/// <summary>
/// A way a print file and the printer it is aimed at disagree.
/// </summary>
/// <remarks>
/// <para>
/// <b>A vocabulary, not a sentence.</b> The wording a person reads is a resource key chosen from
/// this, and the numbers in it come from the rows the reader already has - which is what keeps the
/// comparison out of the business of formatting a nozzle diameter for a Danish reader.
/// </para>
/// <para>
/// <b>Two severities, and the split is wear against waste</b> - see
/// <see cref="PrintCompatibilitySeverity"/>. It is not the split the original design assumed:
/// nozzle size was expected to be the headline check and turns out to be the mild one, because
/// nothing it can cause is permanent.
/// </para>
/// </remarks>
public enum PrintCompatibilityFinding
{
    /// <summary>Not a finding. Present so a default-valued value is not silently a real one.</summary>
    Undefined = 0,

    /// <summary>
    /// The print uses abrasive filament and the printer's nozzle is not hardened.
    /// </summary>
    /// <remarks>
    /// <b>The one that costs hardware.</b> Fibre-filled filament wears a soft nozzle's bore open,
    /// and the worn nozzle goes on reporting its original diameter afterwards - so the damage is
    /// permanent, invisible, and quietly falsifies
    /// <see cref="NozzleDiameterMismatch"/> for every print that follows.
    /// </remarks>
    AbrasiveFilamentNeedsHardenedNozzle = 1,

    /// <summary>
    /// The print uses abrasive filament, and some but not all of the printer's tools are hardened -
    /// so it may or may not pass through a soft one.
    /// </summary>
    /// <remarks>
    /// <b>The uncertain half of the damage gate, and it warns rather than holding.</b> Which tool an
    /// abrasive filament goes through is settled by the file's tool mapping, which firmware resolves
    /// at print time and this cannot see. Holding on that would stop legitimate prints on the one
    /// machine where somebody most likely fitted the right nozzle to the right tool; saying nothing
    /// would stay silent about the only finding here that can cost hardware. So it is said, and the
    /// person decides.
    /// </remarks>
    AbrasiveFilamentMayUseASoftNozzle = 2,

    /// <summary>
    /// The file was sliced for a printer this one cannot print for.
    /// </summary>
    /// <remarks>
    /// <b>Wear rather than a wasted print, which is why it holds</b> (Henrik, 2026-08-19): a file
    /// sliced for a CoreXY carries accelerations and speeds a bed slinger should not be asked to
    /// sustain. Directional, exactly as firmware has it - the older machine's file on the newer one
    /// is fine and says nothing.
    /// </remarks>
    IncompatiblePrinterModel = 3,

    /// <summary>The file was sliced for a different nozzle diameter than the one fitted.</summary>
    /// <remarks>
    /// <para>
    /// <b>The printer checks this one itself and prompts before it starts</b>, so what a wrong
    /// diameter costs is not a ruined part but a queue that stops and waits for somebody to walk to
    /// the machine and answer. That is the whole reason to say it here: for a queue nobody is
    /// standing at, being told at the moment of queueing is the difference between fixing it and
    /// finding the printer waiting an hour later.
    /// </para>
    /// <para>
    /// The prompt is a default rather than a guarantee - firmware's <c>hw_check_nozzle</c> is
    /// <c>Ignore</c>/<c>Warning</c>/<c>Abort</c> and ships as <c>Warning</c>, so it can be turned off
    /// and can be clicked through. The printer's diameter is also a setting somebody maintains
    /// rather than a measurement, so this catches "you forgot to tell the printer" at least as often
    /// as "you picked the wrong file".
    /// </para>
    /// </remarks>
    NozzleDiameterMismatch = 4,

    /// <summary>The file was sliced for a high-flow hotend and the fitted one is standard.</summary>
    /// <remarks>
    /// <b>Directional.</b> A high-flow file asks a standard hotend for more melt than it can
    /// deliver and under-extrudes; a standard file on a high-flow hotend leaves capacity unused and
    /// is not worth mentioning.
    /// </remarks>
    HighFlowNozzleRequired = 5,
}
