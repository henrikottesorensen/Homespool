namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// The one place that knows a printer sends its fingerprint in two different lengths, and reduces
/// both to the single value this service identifies it by.
/// </summary>
/// <remarks>
/// <para>
/// The firmware stores one fingerprint buffer and serialises it two ways. <c>/p/register</c>'s JSON
/// body carries all <c>FINGERPRINT_SIZE</c> (50) characters; every HTTP header carries only
/// <c>FINGERPRINT_HDR_SIZE</c> (16), because <c>BasicRequest</c> and <c>UpgradeRequest</c> both build
/// the <c>Fingerprint</c> header with an explicit <c>HeaderOut::size_limit</c>
/// (<c>src/connect/connect.cpp:137</c> and <c>:164</c>, firmware <c>v6.6.0</c>, SHA <c>e96ce2b</c>).
/// The truncation is unconditional - not per-endpoint, per-version or per-model - so the short form is
/// always an exact prefix of the long one.
/// </para>
/// <para>
/// <b>Identity is therefore the short form.</b> It is the only one present on every request, so it is
/// what the enrolled credential is keyed on. Keying on the long form is what made a code-exchange
/// enrollment unable to authenticate at all, and made the same physical printer look like two
/// printers across the two channels.
/// </para>
/// <para>
/// Truncating rather than prefix-matching keeps the lookup an ordinary indexed equality. Matching
/// "is one column a prefix of another, in either direction" would not use an index and would have to
/// reason about which of two stored values is the longer.
/// </para>
/// </remarks>
public static class PrinterFingerprint
{
    /// <summary>The header form's length: firmware's <c>FINGERPRINT_HDR_SIZE</c>.</summary>
    public const int KeyLength = 16;

    /// <summary>
    /// The identifying key for a fingerprint in either form. Values shorter than
    /// <see cref="KeyLength"/> are returned unchanged rather than padded - a printer that sends one is
    /// not a printer we know, and it should fail the lookup rather than be silently reshaped into
    /// something that might match.
    /// </summary>
    public static string Key(string fingerprint) =>
        fingerprint.Length <= KeyLength ? fingerprint : fingerprint[..KeyLength];
}
