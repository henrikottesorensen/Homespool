using System;
using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Middleware;

/// <summary>
/// Removes the forwarded headers from any request whose peer is not the proxy by name, so that the
/// framework middleware after it has nothing to believe.
/// </summary>
/// <remarks>
/// <para>
/// Runs only when <see cref="XForwardedOptions.ProxyHost"/> is set, and in front of
/// <c>UseForwardedHeaders</c>, which still makes its own check against the known networks - a peer
/// must pass both. Removing the headers rather than skipping the framework middleware, because the
/// branch that decides whether it runs is taken synchronously and the answer here may need a lookup.
/// </para>
/// <para>
/// The header names are read from the framework's options, which <see cref="ForwardedHeadersConfigurator"/>
/// sets, so what is removed here is exactly what would otherwise be read.
/// </para>
/// </remarks>
public sealed class ProxyPeerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ProxyHostAddresses _proxy;
    private readonly ForwardedHeadersOptions _forwarded;

    public ProxyPeerMiddleware(RequestDelegate next, ProxyHostAddresses proxy, IOptions<ForwardedHeadersOptions> forwarded)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(forwarded);

        _next = next;
        _proxy = proxy;
        _forwarded = forwarded.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress? peer = context.Connection.RemoteIpAddress;

        if (peer is null || !await _proxy.IsProxyAsync(peer, context.RequestAborted))
        {
            context.Request.Headers.Remove(_forwarded.ForwardedForHeaderName);
            context.Request.Headers.Remove(_forwarded.ForwardedProtoHeaderName);
            context.Request.Headers.Remove(_forwarded.ForwardedHostHeaderName);
            context.Request.Headers.Remove(_forwarded.ForwardedPrefixHeaderName);
        }

        await _next(context);
    }
}
