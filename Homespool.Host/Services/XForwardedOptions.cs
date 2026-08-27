namespace Homespool.Host.Services;

/// <summary>
/// Which proxy this deployment trusts, and what it is allowed to say about the client. Bound from the
/// <c>XForwarded</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Homespool ships its own nginx in <c>compose.yaml</c> and terminates user TLS there, so the request
/// Kestrel sees has the proxy's address and (internally) plain HTTP. Without honouring the proxy's
/// headers, every absolute URL built from <c>Request.Scheme</c> — eight of them, all in confirmation
/// and password-reset mail — says <c>http://</c>, and the one place that logs a client address logs
/// the proxy's.
/// </para>
/// <para>
/// <b>The whole design rests on this being unusable by anyone but our own proxy.</b> Forwarded headers
/// are ordinary request headers: anyone can send them. They mean something only because the immediate
/// peer is checked against <see cref="KnownProxies"/>/<see cref="KnownNetworks"/> first.
/// </para>
/// <para>
/// <b>Configure neither and the middleware is not registered at all</b>, which is deliberate and not
/// merely tidy. ASP.NET performs the peer check only when at least one known proxy or network is
/// present; with both lists empty it skips the check and honours the headers from <i>any</i> client.
/// So "trust nothing" and "trust everything" are the same configuration as far as the framework is
/// concerned, and the difference has to be made by leaving the middleware out. Measured, not assumed:
/// unconfigured, a loopback client's <c>X-Forwarded-Proto</c> was honoured; with <c>10.0.0.0/8</c>
/// trusted instead, the identical request was ignored. <c>Program</c> also warns, because a
/// deployment behind a proxy with nothing configured is silently wrong - mail keeps saying
/// <c>http://</c>.
/// </para>
/// </remarks>
public class XForwardedOptions
{
    public const string SectionName = "XForwarded";

    /// <summary>
    /// The single header trusted to carry the client's address. Defaults to <c>X-Real-IP</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>X-Real-IP</c> only, deliberately — not <c>X-Forwarded-For</c></b> (Henrik, 2026-07-29:
    /// <i>"since we are controlling the proxy setting, let's ONLY trust x-real-ip"</i>). This removes a bug
    /// class rather than configuring around one.
    /// </para>
    /// <para>
    /// <c>X-Forwarded-For</c> is a <b>comma-separated chain</b>, and the classic vulnerability is
    /// choosing the wrong entry from it: a client sends <c>X-Forwarded-For: 1.2.3.4</c>, the proxy
    /// <i>appends</i> its view, and anything that reads the leftmost entry has just believed the attacker.
    /// <c>X-Real-IP</c> is a single value our own nginx <b>overwrites</b> with
    /// <c>proxy_set_header X-Real-IP $remote_addr;</c>, so there is no list and no entry to choose
    /// wrongly. Because we ship the proxy configuration, we can rely on it being set.
    /// </para>
    /// <para>
    /// A consequence worth stating: a client-supplied <c>X-Forwarded-For</c> is now <b>ignored
    /// entirely</b>, whatever it contains. Change this only for a deployment fronted by something other
    /// than the shipped nginx, and only knowing the above.
    /// </para>
    /// </remarks>
    public string ClientAddressHeader { get; set; } = "X-Real-IP";

    /// <summary>
    /// Addresses of proxies whose forwarded headers are believed. Empty by default.
    /// </summary>
    /// <remarks>
    /// A container's address is assigned by Docker and is not stable, so
    /// <see cref="KnownNetworks"/> is usually the right lever for the shipped compose stack; this
    /// exists for a proxy on a fixed address.
    /// </remarks>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// CIDR ranges whose forwarded headers are believed, e.g. <c>172.28.0.0/16</c>. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one the shipped stack sets, and shipping the proxy is what makes it knowable:
    /// <c>compose.yaml</c> defines the network, so it can pin the subnet and trust exactly that.
    /// Without a proxy of our own there would be nothing safe to put here, which is why this was the
    /// blocking difficulty before.
    /// </para>
    /// <para>
    /// <b>Do not "fix" an inert configuration by trusting everything.</b> Clearing the known networks
    /// — the remedy most readily found online — means believing an address header from any client that
    /// sends one, which is worse than not reading it at all: it launders attacker input into logs and
    /// into any future per-IP rate limiting.
    /// </para>
    /// </remarks>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// How many proxy hops to walk back through. Default 1, matching one nginx in front.
    /// </summary>
    /// <remarks>
    /// Raise only when there genuinely are more trusted hops. Each increment is another entry taken on
    /// trust, and with <see cref="ClientAddressHeader"/> being single-valued anything above 1 has
    /// nothing further to read anyway.
    /// </remarks>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// True once something has been configured to trust, i.e. the middleware can do anything at all.
    /// </summary>
    /// <remarks>
    /// Used to warn at startup rather than to disable anything: the failure mode this catches is a
    /// deployment behind a proxy where nothing is trusted, which is silent — mail keeps saying
    /// <c>http://</c> and nobody connects that to a missing setting.
    /// </remarks>
    public bool TrustsAnything => KnownProxies.Length > 0 || KnownNetworks.Length > 0;
}
