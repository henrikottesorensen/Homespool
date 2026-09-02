using System;

using Microsoft.Extensions.Configuration;

namespace Homespool.Host.Listeners;

/// <summary>
/// Which ports this process listens on, bound from the <c>Listeners</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>One listener per credential class</b>, which is the rule the split is keyed on rather than
/// "printers versus everything": a printer authenticates with a fingerprint and a token, a user with
/// a cookie or a personal access token, and a camera will one day arrive with a third.
/// Separating them means a leaked printer token
/// reaches no application surface, because those routes do not exist on that listener at all.
/// </para>
/// <para>
/// <b>These are the ports inside this process, not the ports anything connects to.</b> A printer is told
/// what <c>PrusaConnect:PrinterHost</c>/<c>:PrinterPort</c> say — typically 443, published onto
/// <see cref="PrinterPort"/> by Compose — and a user arrives through nginx. That indirection is
/// exactly why the boundary is enforced on the local port rather than on the <c>Host</c> header; see
/// <see cref="ListenerSegregationMiddleware"/>.
/// </para>
/// </remarks>
public class ListenerOptions
{
    public const string SectionName = "Listeners";

    /// <summary>
    /// Binds these straight from configuration, for the places that need a port before the container
    /// exists.
    /// </summary>
    /// <remarks>
    /// Kestrel is configured, the HTTPS-redirection port is pinned and the forwarded-header scope is
    /// decided while the service collection is still being built, so none of them can resolve
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>. Reading the section twice is
    /// cheaper than the alternative, which is a second copy of the port numbers.
    /// </remarks>
    /// <param name="configuration">The configuration the section is read from.</param>
    /// <returns>The bound options, with the defaults above for anything unset.</returns>
    public static ListenerOptions ReadFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ListenerOptions listeners = new();
        configuration.GetSection(SectionName).Bind(listeners);

        return listeners;
    }

    /// <summary>
    /// The listener carrying the printer protocol: <c>/p/*</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plain HTTP, with nginx terminating the printer's TLS in front of it</b> — it used to serve
    /// the leaf minted from our own authority, and stopped because
    /// <see cref="System.Net.Security.SslStream"/> ignores the <c>max_fragment_length</c> a printer
    /// negotiates while OpenSSL honours it, which broke every file transfer. Nothing but the proxy
    /// reaches this port, so it is not published.
    /// </para>
    /// <para>
    /// <c>PrusaConnect:PrinterTls=false</c> is the exception, and it is a capture tool rather than a
    /// deployment: no leaf is issued, no proxy stands in front, and Compose publishes this port
    /// directly so the wire can be read.
    /// </para>
    /// <para>
    /// Above 1024 because the container runs as a non-root user (<c>Dockerfile</c>, <c>USER
    /// $APP_UID</c>) and could not bind 443 even if we wanted it to.
    /// </para>
    /// </remarks>
    public int PrinterPort { get; set; } = 15443;

    /// <summary>
    /// The plain-HTTP listener for people: pages, <c>/api</c>, <c>/health</c>, everything else.
    /// </summary>
    /// <remarks>
    /// <b>Plain HTTP on purpose</b>, as <see cref="PrinterPort"/> now is too — TLS for users is
    /// terminated in front of this process by the proxy, which takes a certificate of the operator's
    /// choosing. That the two listeners are terminated by the same nginx does not make them one
    /// certificate: the printer's ECDSA leaf must never be served to a browser, since it is signed by
    /// a private authority no browser has any reason to trust, so the proxy holds two and serves each
    /// on its own port. 8080 matches the base image's <c>ASPNETCORE_HTTP_PORTS</c>, so a deployment
    /// that never sets this keeps the port it had.
    /// </remarks>
    public int UserPort { get; set; } = 8080;

    /// <summary>
    /// An optional HTTPS listener for people, using the ASP.NET development certificate or whatever
    /// <c>Kestrel:Certificates:Default</c> names. Null — the default — means no such listener.
    /// </summary>
    /// <remarks>
    /// Exists for <c>dotnet run --launch-profile https</c> and for a deployment that terminates TLS
    /// in this process rather than in a proxy. It is <i>not</i> the printer listener and never
    /// carries the printer's certificate.
    /// </remarks>
    public int? UserHttpsPort { get; set; }

    /// <summary>
    /// The plain-HTTP listener carrying encrypted downloads: <c>/f/*</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plain HTTP by design, and never behind the printer's TLS.</b> A printer on the pre-websocket
    /// transport fetches a file over a plain connection it opens itself; the body is AES-CTR
    /// ciphertext, so what crosses this port is unreadable without a key that only ever travelled
    /// inside the command. It gets its own port so the door that is deliberately unencrypted serves
    /// exactly one thing, and so the printer's own listener can stay TLS-terminated regardless.
    /// </para>
    /// <para>
    /// <b>Not integrity-protected</b> - CTR is malleable, and firmware checks nothing about the body
    /// but its length. An on-path attacker cannot read the gcode but can corrupt it, bit for bit.
    /// That makes this a LAN proposition; publish this port to the internet knowingly or not at all.
    /// </para>
    /// <para>
    /// 15080 beside the printer's 15443: the same range, the suffix saying plain against TLS.
    /// </para>
    /// </remarks>
    public int TransferPort { get; set; } = 15080;

    /// <summary>
    /// Throws unless the ports describe a boundary that can actually exist.
    /// </summary>
    /// <remarks>
    /// Two credential classes sharing a port is not a misconfiguration that degrades gracefully — it
    /// is the segregation silently switched off, with every route answering on the one listener. This
    /// runs while Kestrel is being configured, so a deployment that gets it wrong fails to start
    /// rather than serving printer endpoints to the internet.
    /// </remarks>
    public void Validate()
    {
        if (PrinterPort == UserPort || PrinterPort == UserHttpsPort)
        {
            throw new InvalidOperationException(
                $"Listeners:PrinterPort ({PrinterPort}) must differ from the user-facing ports "
                + $"(UserPort {UserPort}, UserHttpsPort {UserHttpsPort?.ToString() ?? "none"}). Sharing a port "
                + "would put printer endpoints and application endpoints on one listener, which is the "
                + "separation this exists to keep.");
        }

        if (TransferPort == UserPort || TransferPort == UserHttpsPort || TransferPort == PrinterPort)
        {
            throw new InvalidOperationException(
                $"Listeners:TransferPort ({TransferPort}) must differ from every other listener "
                + $"(UserPort {UserPort}, UserHttpsPort {UserHttpsPort?.ToString() ?? "none"}, PrinterPort {PrinterPort}). "
                + "It is the one deliberately plain-HTTP door, and it must serve nothing but transfers.");
        }
    }
}
