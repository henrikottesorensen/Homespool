using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Listeners;

/// <summary>
/// Refuses an endpoint reached on a listener it does not belong to, so a leaked credential of one
/// class cannot reach the surface belonging to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>The listener is identified by <see cref="ConnectionInfo.LocalPort"/>, not by the <c>Host</c>
/// header</b>, and that is the whole of the security argument. The port a connection arrived on is a
/// property of the socket: a client cannot claim a different one, and no header it sends changes it.
/// It is the same reasoning that scopes forwarded headers structurally rather than by a flag — a flag
/// can be set wrong, a listener cannot.
/// </para>
/// <para>
/// <b>Routing constraints on the host header were the original design, and they do not survive
/// contact with the deployment.</b> <c>RequireHost("*:15443")</c> matches what the client wrote in
/// the <c>Host</c> header, which is neither trustworthy nor, more mundanely, correct: Compose
/// publishes 443 onto the printer listener, so a printer connects on 443 and says so, and nginx passes the
/// user's own public hostname with no port at all. Both would be refused by a rule that reads the
/// header, while an attacker who simply types the right port would be admitted. The header describes
/// what the client believes it connected to; the port describes what it did.
/// </para>
/// <para>
/// <b>404, not 403</b>, and no body: on the wrong listener the route genuinely does not exist, and
/// saying "forbidden" would confirm to whoever is probing that it exists somewhere else.
/// </para>
/// <para>
/// Runs immediately after routing, so a refusal costs no authentication, no rate-limiter permit and
/// no database work — and before the setup gate, so a probe on the wrong listener gets the same 404
/// before an administrator exists as after.
/// </para>
/// </remarks>
public sealed class ListenerSegregationMiddleware : IMiddleware
{
    private readonly ListenerOptions _listeners;
    private readonly ILogger<ListenerSegregationMiddleware> _logger;

    public ListenerSegregationMiddleware(IOptions<ListenerOptions> listeners,
                                         ILogger<ListenerSegregationMiddleware> logger)
    {
        _listeners = listeners.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Endpoint? endpoint = context.GetEndpoint();

        if (endpoint is null)
        {
            // Nothing matched, so there is nothing to segregate: the 404 arrives on its own.
            await next(context);

            return;
        }

        // The classification an endpoint was given, or - for the handful the framework adds outside
        // any convention builder we can reach, such as MapStaticAssets' file fallback - the one its
        // path implies. The fallback is not a hole: it is the same prefix rule applied to the request
        // instead of the route, so an unclassified endpoint under /p is still printer-only, and an
        // unclassified endpoint anywhere else is still absent from the printer listener.
        ListenerClass belongsTo = endpoint.Metadata.GetMetadata<ListenerRequirement>()?.Listener
                                  ?? ListenerSegregation.ClassFor(context.Request.Path);

        ListenerClass arrivedOn = ClassOf(context.Connection.LocalPort);

        if (belongsTo == arrivedOn)
        {
            await next(context);

            return;
        }

        if (belongsTo == ListenerClass.Printer)
        {
            // Almost always a real printer pointed at the wrong port, and a bare 404 tells whoever
            // provisioned it nothing at all. The firmware burns one of three registration retries on
            // this, so it is worth being findable in the log.
            _logger.LogWarning("A request for the printer endpoint {Path} arrived on port {Port}, which is not the "
                               + "printer listener ({PrinterPort}). Printers must use the port published onto "
                               + "Listeners:PrinterPort; the address in their ini is PrusaConnect:PrinterHost.",
                               context.Request.Path, context.Connection.LocalPort, _listeners.PrinterPort);
        }
        else
        {
            _logger.LogDebug("Refused {Path} on the printer listener; it belongs to the user listener.",
                             context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>
    /// Which listener a local port is.
    /// </summary>
    /// <remarks>
    /// Deliberately an exact match on the printer port with everything else falling to
    /// <see cref="ListenerClass.User"/>, rather than a lookup that could fail open: a port we cannot
    /// identify must never be able to serve printer endpoints.
    /// </remarks>
    private ListenerClass ClassOf(int localPort)
    {
        return localPort == _listeners.PrinterPort ? ListenerClass.Printer : ListenerClass.User;
    }
}
