using System;

namespace Homespool.Host.Listeners;

/// <summary>
/// Which ports this process listens on, bound from the <c>Listeners</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>One listener per credential class</b>, which is the rule the split is keyed on rather than
/// "printers versus everything": a printer authenticates with a fingerprint and a token, a user with
/// a cookie or a personal access token, and a camera will one day arrive with a third
/// (<c>notes/tls-by-default.md</c>, decision 3a). Separating them means a leaked printer token
/// reaches no application surface, because those routes do not exist on that listener at all.
/// </para>
/// <para>
/// <b>These are the ports inside this process, not the ports anything dials.</b> A printer is told
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
    /// The TLS listener printers connect to, carrying the leaf minted from our own authority.
    /// </summary>
    /// <remarks>
    /// Above 1024 because the container runs as a non-root user (<c>Dockerfile</c>, <c>USER
    /// $APP_UID</c>) and could not bind 443 even if we wanted it to. Compose publishes the port a
    /// printer actually dials onto this one.
    /// </remarks>
    public int PrinterPort { get; set; } = 15443;

    /// <summary>
    /// The plain-HTTP listener for people: pages, <c>/api</c>, <c>/health</c>, everything else.
    /// </summary>
    /// <remarks>
    /// <b>Plain HTTP on purpose.</b> TLS for users is terminated in front of this process by the
    /// operator's proxy, which takes a certificate of their choosing and does not ask where it came
    /// from — the printer's ECDSA leaf must never be served to a browser, since it is signed by a
    /// private authority no browser has any reason to trust. 8080 matches the base image's
    /// <c>ASPNETCORE_HTTP_PORTS</c>, so a deployment that never sets this keeps the port it had.
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
    }
}
