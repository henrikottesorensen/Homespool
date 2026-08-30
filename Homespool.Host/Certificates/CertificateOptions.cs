using System.Collections.Generic;
using System.Linq;

namespace Homespool.Host.Certificates;

/// <summary>
/// Where the printer-facing certificate authority lives and how it is issued, bound from the
/// <c>Certificates</c> configuration section.
/// </summary>
/// <remarks>
/// This is the anchor a provisioned printer trusts, and <c>custom_cert</c> is exclusive — it replaces
/// Prusa's roots wholesale — so this CA becomes each printer's <b>entire</b> trust store. Its private
/// key is correspondingly the most sensitive secret in the deployment: whoever holds it can mint a
/// certificate for any name those printers will believe, permanently and undetectably.
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
    /// Directory holding the leaf and its key in PEM, for nginx to read. Relative paths resolve
    /// against the content root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate directory from <see cref="Directory"/> for one reason: the proxy container mounts
    /// this one, and the authority's private key must never be inside anything it mounts.</b> Both
    /// files could sit beside the authority — they did when nothing consumed them — and sharing that
    /// directory would hand the internet-facing container the key that mints certificates every
    /// provisioned printer trusts, permanently and undetectably. The leaf is the opposite kind of
    /// secret: losing it costs a reissue and a proxy reload, because the printers trust the authority
    /// rather than the leaf. That asymmetry is the whole reason this deployment uses a CA at all, and
    /// this is where it pays.
    /// </para>
    /// <para>
    /// <b>These two files are world-readable where everything else here is owner-only</b>, which is
    /// deliberate and is the cost of the arrangement above: nginx runs as its own uid and could not
    /// otherwise read a key the application wrote. What that exposes is scoped to the volume, which
    /// only these two containers mount, and to a secret that is replaceable without touching a
    /// printer.
    /// </para>
    /// </remarks>
    public string ProxyDirectory { get; set; } = "data/proxy-certificates";

    /// <summary>
    /// Passphrase encrypting the authority's private key at rest. Required: the key is never
    /// minted, loaded or migrated without one — startup refuses instead — so there is no
    /// plaintext-at-rest mode to fall into by accident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this defends is a copied <c>data/</c> volume</b> — a backup that wanders, a
    /// <c>docker cp</c>, a file-read bug — which is the realistic way this key leaks. The passphrase
    /// lives in the environment (<c>.env</c> on the shipped stack), outside the volume, so a copy of
    /// the data no longer carries the key that every provisioned printer trusts. It does not defend a
    /// compromised host, which holds both halves; nothing at this layer can.
    /// </para>
    /// <para>
    /// <b>Losing it is losing the authority.</b> There is no recovery path and deliberately no
    /// re-mint on a failed decrypt: a fresh authority would strand every provisioned printer until
    /// someone visits each with a USB stick. Back the value up with the same care as the key file
    /// itself, and separately from <c>data/</c> — a backup holding both has defended nothing.
    /// </para>
    /// </remarks>
    public string AuthorityPassphrase { get; set; } = string.Empty;

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
    /// transferred over Connect, so replacing the anchor means a USB
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
    /// How long the Data Protection key-protection certificate is valid, in days. Default fifteen
    /// years, matching the authority.
    /// </summary>
    /// <remarks>
    /// Long deliberately: nothing verifies that certificate, so an expiry buys no security and would
    /// only ever surface as an outage. See <see cref="DataProtectionCertificate"/>.
    /// </remarks>
    public int KeyProtectionValidityDays { get; set; } = 5475;

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

    /// <summary>
    /// CIDR ranges that exist only inside this deployment — a container network, typically. Addresses
    /// in them are never offered as somewhere a printer could reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configuration rather than a constant, because the range is a deployment's to choose.</b>
    /// <c>compose.yaml</c> pins its own network and invites changing it if it collides with something
    /// on the host - and a hardcoded rule would then stop recognising the container's address and go
    /// back to offering it as somewhere to connect to, which is precisely the trap this closes. The shipped
    /// stack feeds this from the same <c>PROXY_NETWORK</c> that pins the network, so the two cannot
    /// disagree.
    /// </para>
    /// <para>
    /// <b>The default is Docker's, not RFC 1918's</b>, and deliberately narrower than "private": a
    /// household LAN on 192.168/16 or 10/8 is exactly what a printer <i>can</i> reach, so those must
    /// never be in here. A LAN that genuinely uses 172.16/12 should empty this list rather than have
    /// its own addresses mistaken for a container's - which is the second reason it is configurable.
    /// </para>
    /// <para>
    /// Deliberately not <c>XForwarded:KnownNetworks</c>, which looks like the same value and is not:
    /// that names whichever proxy may speak for a client, and it is legitimate for that to be a
    /// machine elsewhere on the LAN. Treating such a range as container-internal would filter the very
    /// addresses printers use.
    /// </para>
    /// </remarks>
    public string[] ContainerNetworks { get; set; } = ["172.16.0.0/12"];

    /// <summary>
    /// <see cref="ContainerNetworks"/> parsed, with anything unparseable dropped rather than fatal.
    /// </summary>
    /// <remarks>
    /// A typo here must not stop the server: the consequence of ignoring one entry is an address
    /// offered that should not have been, and the consequence of throwing is a deployment that will
    /// not start. The first is visible on a page; the second happens at 2am.
    /// </remarks>
    public IReadOnlyList<System.Net.IPNetwork> ParsedContainerNetworks =>
        [.. (ContainerNetworks ?? []).Select(TryParseNetwork).Where(n => n is not null).Select(n => n!.Value)];

    private static System.Net.IPNetwork? TryParseNetwork(string cidr)
    {
        return System.Net.IPNetwork.TryParse(cidr, out System.Net.IPNetwork parsed) ? parsed : null;
    }
}
