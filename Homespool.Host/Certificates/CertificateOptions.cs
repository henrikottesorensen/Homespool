namespace Homespool.Host.Certificates;

/// <summary>
/// Where the printer-facing certificate authority lives and how it is issued, bound from the
/// <c>Certificates</c> configuration section.
/// </summary>
/// <remarks>
/// This is the anchor a provisioned printer trusts, and <c>custom_cert</c> is exclusive — it replaces
/// Prusa's roots wholesale — so this CA becomes each printer's <b>entire</b> trust store. Its private
/// key is correspondingly the most sensitive secret in the deployment: whoever holds it can mint a
/// certificate for any name those printers will believe, permanently and undetectably. See
/// <c>notes/tls-by-default.md</c>.
/// </remarks>
public class CertificateOptions
{
    public const string SectionName = "Certificates";

    /// <summary>
    /// Directory holding the CA and the printer leaf. Relative paths resolve against the content root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <c>data/</c> for the same reason uploads are: that is what <c>compose.yaml</c> mounts as
    /// a volume, and a CA regenerated on every container replacement would strand every printer
    /// provisioned from the previous one.
    /// </para>
    /// <para>
    /// <b>On disk rather than in the database</b>, deliberately, though the database already holds
    /// Data Protection keys and would have been the shorter path. A private key in the SQLite file is
    /// a private key inside every copy of that file — and the operator instruction for this project is
    /// "back up <c>data/</c>", which people do by copying it somewhere. A separate file can be
    /// excluded, permissioned, and noticed; a row cannot.
    /// </para>
    /// </remarks>
    public string Directory { get; set; } = "data/certificates";

    /// <summary>
    /// How long the certificate authority is valid, in days. Default fifteen years.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fifteen years</b> (Henrik, 2026-07-29), sitting between the ten first proposed and the
    /// twenty of Prusa's own root (valid 2024-04-16 to 2044-04-11) — the vendor's choice under this
    /// exact constraint, and the reason twenty was on the table at all. The reasoning below argues
    /// only against <i>short</i>; it gives no upper bound, so the top end is a judgement call rather
    /// than a derivation.
    /// </para>
    /// <para>
    /// Renewal is expensive here in a way it is not for a web certificate: a <c>.der</c> cannot be
    /// transferred over Connect (<c>notes/tls-by-default.md</c>), so replacing the anchor means a USB
    /// visit to every printer. <b>And there is no revocation either</b> — nothing on the printer does
    /// CRL or OCSP — so a shorter life does not reduce the cost of a compromised key; that is a
    /// fleet-wide visit whatever the validity says. Lifetime therefore only schedules <i>guaranteed</i>
    /// pain on a known date while mitigating nothing.
    /// </para>
    /// <para>
    /// The usual counterargument, cryptographic agility, does not apply: the firmware compiles exactly
    /// one ciphersuite and one curve, so if P-256 falls there is nothing to migrate to. And the
    /// failure misleads — an expired anchor surfaces as <c>-9984</c>, naming neither the certificate
    /// nor the clock — on a date when nobody will remember this was set up.
    /// </para>
    /// <para>
    /// Expiry is genuinely enforced, not nominal: <c>MBEDTLS_HAVE_TIME_DATE</c> is defined in the same
    /// config header that pins the ciphersuite, and the printer runs an SNTP client, so it knows the
    /// real date. There is a window at boot before SNTP syncs when its clock may be wrong; the
    /// firmware retries, so that resolves itself.
    /// </para>
    /// </remarks>
    public int AuthorityValidityDays { get; set; } = 5475;

    /// <summary>
    /// How long an issued leaf is valid, in days. Default two years.
    /// </summary>
    /// <remarks>
    /// Shorter than the authority because this one <i>can</i> be replaced without touching a printer:
    /// the printer trusts the CA, so a reissued leaf needs only a server restart. That asymmetry is
    /// the entire reason for choosing a CA over a self-signed leaf.
    /// </remarks>
    public int LeafValidityDays { get; set; } = 730;

    /// <summary>
    /// Subject name given to the generated authority.
    /// </summary>
    /// <remarks>
    /// Never presented to a user and never matched against anything — the printer checks the
    /// <i>leaf</i>'s SAN, not this. It exists so a human inspecting <c>connect.der</c> can tell what
    /// it is.
    /// </remarks>
    public string AuthorityName { get; set; } = "Homespool printer CA";
}
