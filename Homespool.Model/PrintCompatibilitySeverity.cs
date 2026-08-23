namespace Homespool.Model;

/// <summary>
/// What a <see cref="PrintCompatibilityFinding"/> costs, and therefore what happens about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two values, split on wear against waste</b> (Henrik, 2026-08-19). A finding that costs
/// hardware holds the queue; one that costs filament and hours is said out loud and the person
/// decides. Nothing here overrides <c>"we gotta trust what the printer is reporting"</c> - both
/// severities take the printer's own report at face value, and disagree only about what to do when
/// it and the file are at odds.
/// </para>
/// <para>
/// <b>Deliberately not a third "cannot tell" value.</b> A comparison with a missing side produces
/// no finding at all, which is a different thing from a mild one: an unreadable file must not
/// generate a row for somebody to dismiss.
/// </para>
/// </remarks>
public enum PrintCompatibilitySeverity
{
    /// <summary>Not a severity. Present so a default-valued value is not silently a real one.</summary>
    Undefined = 0,

    /// <summary>
    /// Say so, and let the print go. Filament and hours are at stake, and the printer's report may
    /// be the thing that is stale.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Hold the queue behind it until somebody or something changes. The cost is hardware, and
    /// unlike a bad print it does not undo.
    /// </summary>
    /// <remarks>
    /// <b>A hold, not a refusal</b> - the queue entry survives, and the hold lifts by itself when
    /// the printer's next <c>INFO</c> reports hardware that agrees. That is the same shape as the
    /// free-space hold, and it is what keeps this from being a check that has to be argued with.
    /// </remarks>
    Hold = 2,
}
