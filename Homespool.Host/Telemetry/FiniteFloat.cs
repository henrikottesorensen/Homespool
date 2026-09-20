namespace Homespool.Host.Telemetry;

/// <summary>
/// The rule for a float on its way from an update into stored state: infinity and NaN are not
/// readings, and are stored as no reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>They arrive without anything being malformed.</b> A JSON number too large for a float parses
/// to infinity rather than failing - <c>1e39</c> is enough - so any protocol edge that reads a float
/// can hand one over, from a printer that is authenticated and not trusted.
/// </para>
/// <para>
/// <b>Neither can be allowed to settle.</b> SQLite refuses to store NaN, which would fail the write
/// it shares with every other printer's samples; it stores infinity, which then fails every response
/// that serializes the row until retention removes it.
/// </para>
/// <para>
/// <b>Applied where an update is written into an entity, not at the edges</b>, so that it is said
/// once and holds for a protocol that does not exist yet. An edge may still have its own opinion
/// about a value - Prusa Connect's zero nozzle means unreported - but it cannot forget this one.
/// </para>
/// </remarks>
public static class FiniteFloat
{
    /// <summary>The value when it is a number, otherwise null.</summary>
    public static float? OrNull(float? value)
    {
        return value is { } number && float.IsFinite(number) ? number : null;
    }
}
