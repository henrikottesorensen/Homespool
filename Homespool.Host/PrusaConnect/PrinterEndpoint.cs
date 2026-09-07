using System;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Which way in a provisioning bundle points one printer: the port it should connect to, and whether it
/// verifies anything when it gets there.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the answer stopped being a deployment-wide fact.</b> Every file that
/// composes a bundle used to read <see cref="PrusaConnectOptions.PrinterPort"/> and
/// <see cref="PrusaConnectOptions.PrinterTls"/> for itself, which was correct while a deployment had
/// one printer listener. It now may have two, and which one a given printer is sent to is decided per
/// printer — so the choice is made once, here, and carried rather than re-derived by each of the ini,
/// the readme and the archive. Three readings of one setting is three places for them to disagree.
/// </para>
/// <para>
/// <b><see cref="Legacy"/> is not <c>PrinterTls = false</c> wearing another name.</b> That setting
/// takes TLS away from the whole deployment and issues no certificate at all; this sends one printer
/// through a second listener while every other keeps the first. They coincide only in what the ini ends
/// up saying.
/// </para>
/// </remarks>
/// <param name="Port">The port to write into the ini — what the printer connects to from outside.</param>
/// <param name="Tls">Whether the printer should use TLS, and so whether it verifies a certificate.</param>
public readonly record struct PrinterEndpoint(int Port, bool Tls)
{
    /// <summary>
    /// The endpoint this deployment offers by default: TLS-terminated unless the deployment has turned
    /// printer TLS off wholesale.
    /// </summary>
    public static PrinterEndpoint Default(PrusaConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PrinterEndpoint(options.PrinterPort, options.PrinterTls);
    }

    /// <summary>
    /// The plaintext endpoint, for a printer whose firmware cannot load a custom certificate. Null when
    /// this deployment has not opened one, which is the default.
    /// </summary>
    /// <remarks>
    /// Always <c>Tls = false</c>: there is nothing listening for a TLS handshake on that port, and a
    /// bundle claiming otherwise would produce exactly the silent, unexplainable failure the listener
    /// exists to route around.
    /// </remarks>
    public static PrinterEndpoint? Legacy(PrusaConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.LegacyPrinterPort is int port ? new PrinterEndpoint(port, false) : null;
    }

    /// <summary>
    /// Whether a bundle through this endpoint carries a trust anchor, and whether the address written
    /// into it has to be one the certificate covers.
    /// </summary>
    /// <remarks>
    /// The two travel together on purpose. A printer that verifies nothing needs no
    /// <c>connect.der</c>, and refusing an address because a certificate omits it would be refusing
    /// on the strength of a certificate nothing in this bundle will ever present.
    /// </remarks>
    public bool CarriesATrustAnchor => Tls;
}
