using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Certificates;

/// <summary>
/// Asks a TLS listener which certificate it serves for a name, behind an interface so the judgement
/// that uses the answer can be tested without a proxy, a certificate authority or a calendar.
/// </summary>
public interface IPublicCertificateProbe
{
    /// <summary>
    /// What <paramref name="host"/>:<paramref name="port"/> serves when asked for
    /// <paramref name="name"/>.
    /// </summary>
    /// <param name="host">The listener's address or name.</param>
    /// <param name="port">The listener's port.</param>
    /// <param name="name">The name to ask for, as SNI and as the name the certificate must cover.</param>
    /// <param name="cancellationToken">Ends the attempt.</param>
    /// <returns>What was served, or why nothing was.</returns>
    Task<PublicCertificateProbeResult> ProbeAsync(string host, int port, string name, CancellationToken cancellationToken);
}
