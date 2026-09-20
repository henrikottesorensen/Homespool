namespace Homespool.Host.PrusaConnect;

/// <summary>
/// One non-finite number a printer wrote where JSON has no way to write one, and that
/// <see cref="NonFiniteNumberPatcher"/> replaced with its quoted literal.
/// </summary>
/// <param name="StringOrdinal">Which string value of the patched document stands in for it, counting
/// every string value - not property names - in document order from zero. A parsed document keeps no
/// byte offsets, so this is what lets <see cref="PrinterTrafficLog"/> tell a replacement from a
/// <c>"NaN"</c> the printer sent as a string itself.</param>
/// <param name="Spelling">What the printer sent, sign included: <c>nan</c>, <c>-inf</c>,
/// <c>Infinity</c>. ASCII letters and a sign, nothing else - the patcher's grammar admits no other
/// byte, which is what makes it safe to log.</param>
public readonly record struct NonFiniteToken(int StringOrdinal, string Spelling);
