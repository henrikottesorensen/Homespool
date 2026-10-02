namespace Homespool.Host.Certificates;

/// <summary>
/// Which names are meant to be served a publicly-trusted certificate, and where to ask for them,
/// bound from the <c>PublicCertificate</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>The application issues none of these.</b> The certificates are obtained on the host by
/// <c>acme/homespool-renew-cert.sh</c> and served by the proxy; the application only asks the proxy
/// what it serves, the way a browser would, so that a certificate about to expire is told to the people
/// who read the banner and the alert mail rather than to a journal nobody reads.
/// </para>
/// <para>
/// Empty <see cref="Hosts"/>, the ordinary case, means the deployment serves only the self-signed
/// certificates the proxy mints, which expire in ten years, and there is nothing to check.
/// </para>
/// </remarks>
public class PublicCertificateOptions
{
    public const string SectionName = "PublicCertificate";

    /// <summary>
    /// The names, as <c>ACME_HOSTS</c> gives them: separated by semicolons, the same list the renewal
    /// reads, so the two cannot disagree about which names should have a certificate.
    /// </summary>
    public string Hosts { get; set; } = string.Empty;

    /// <summary>
    /// The proxy's name on the application's network. Empty means nothing is checked: outside
    /// <c>compose.yaml</c> there is no proxy to ask.
    /// </summary>
    public string ProxyHost { get; set; } = string.Empty;

    /// <summary>The port the proxy's user listener has inside its container, not the published one.</summary>
    public int ProxyPort { get; set; } = 8443;

    /// <summary>
    /// Days before expiry at which the banner starts saying so. Let's Encrypt issues for 90 days and
    /// the renewal replaces a certificate at 30 remaining, so 21 is a week of failed renewals.
    /// </summary>
    public int WarnDays { get; set; } = 21;

    /// <summary>
    /// Days before expiry at which the check turns Unhealthy, which is what mails and pushes the
    /// administrators. A week, so a slow fix is still in time.
    /// </summary>
    public int AlertDays { get; set; } = 7;
}
