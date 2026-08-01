using System;

using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Listeners;

/// <summary>
/// Which listeners may have their forwarded headers believed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate type for three lines because those three lines decide whether a client can choose
/// its own apparent address.</b> <c>X-Real-IP</c> is trusted absolutely once the middleware runs —
/// that is what forwarded headers are for — so the question of where it runs is the whole of the
/// protection, and it was previously an inline lambda with nothing asserting it.
/// </para>
/// <para>
/// <b>Keyed on the local port, like every other boundary here.</b> The port a connection arrived on
/// is a property of the socket and no header changes it; the same argument
/// <see cref="ListenerSegregationMiddleware"/> makes at greater length. It has to be the port rather
/// than the path in any case, because this runs before routing and there is no endpoint to ask yet.
/// </para>
/// </remarks>
public static class ForwardedHeaderScope
{
    /// <summary>
    /// Whether a request that arrived on <paramref name="localPort"/> may have its forwarded headers
    /// honoured.
    /// </summary>
    /// <param name="localPort">The port the connection arrived on.</param>
    /// <param name="printerPort">The printer listener's port.</param>
    /// <param name="printerListenerIsProxied">
    /// Whether nginx terminates printer TLS in front of that listener — <c>PrusaConnect:PrinterTls</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The user listener always, because nothing else can reach it</b>: its port is not published,
    /// so the only client is the proxy on the container network, and the trusted-network check in
    /// <c>XForwarded:KnownNetworks</c> is the second lock on that door.
    /// </para>
    /// <para>
    /// <b>The printer listener only when nginx stands in front of it</b>, which reverses what decision
    /// 3a recorded and does so because the fact under it changed. Printers used to dial Kestrel
    /// directly, so an <c>X-Real-IP</c> on that listener was written by whoever connected and honouring
    /// it would have let a printer — or anything holding a stolen printer token — claim any address it
    /// liked in the logs and in anything keyed on address. Once the proxy terminates printer TLS the
    /// port is unpublished and the proxy is the only thing that can reach it, so the header is the
    /// proxy's word exactly as it is for users. <c>PrusaConnect:PrinterTls=false</c> puts printers back
    /// on the wire directly and this back to refusing them.
    /// </para>
    /// <para>
    /// Refusing costs a printer's real address in the logs, which is the diagnostic that finds a
    /// misbehaving printer on a LAN — worth having, and not worth inventing.
    /// </para>
    /// </remarks>
    public static bool AppliesTo(int localPort, int printerPort, bool printerListenerIsProxied) =>
        localPort != printerPort || printerListenerIsProxied;

    /// <summary>
    /// The same rule as a predicate over a request, for <c>UseWhen</c>.
    /// </summary>
    /// <remarks>
    /// <b>This exists so that "which property of the connection decides it" is covered by a test.</b>
    /// Written inline at the call site it was not: <see cref="AppliesTo(int, int, bool)"/> takes an
    /// <c>int</c>, so every one of its tests passes just as happily whether the caller reads
    /// <see cref="ConnectionInfo.LocalPort"/> or <see cref="ConnectionInfo.RemotePort"/> — and reading
    /// the remote port would hand the decision to the client, which is the whole thing this guards
    /// against. Moving one line out of the pipeline puts it somewhere a test can reach.
    /// </remarks>
    public static Func<HttpContext, bool> Predicate(int printerPort, bool printerListenerIsProxied) =>
        context => AppliesTo(context.Connection.LocalPort, printerPort, printerListenerIsProxied);
}
