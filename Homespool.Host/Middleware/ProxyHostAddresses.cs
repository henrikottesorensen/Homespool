using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;

namespace Homespool.Host.Middleware;

/// <summary>
/// The addresses <see cref="XForwardedOptions.ProxyHost"/> resolves to, looked up when a request
/// needs them and kept for a few seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>A miss looks again, at most once a second.</b> A proxy recreated on a new address is believed
/// again on its first request after the next lookup rather than when the cache expires, and a peer
/// that is not the proxy - the host, connecting from the bridge gateway - costs one lookup a second
/// however often it asks.
/// </para>
/// <para>
/// <b>The other side of that: an address the proxy has just left stays believed until the answer
/// expires.</b> Only a container on the proxy's network could be holding it by then - the host
/// cannot, its one address there is the gateway - and nothing else joins that network. Asking on
/// every request would close the window at the cost of a lookup per request.
/// </para>
/// <para>
/// <b>The lookup never carries a request's cancellation.</b> An aborted lookup answers empty, and an
/// empty answer is cached like any other; tied to a request, any client that hung up mid-lookup would
/// leave the proxy untrusted until the next one. The resolver bounds the wait itself.
/// </para>
/// <para>
/// <b>The name is resolved as absolute.</b> A container copies the host's search domains, and while
/// the proxy is down Docker's resolver does not know the bare name, so it would be tried under each
/// of them - measured: a stopped <c>proxy</c> answered as <c>127.0.0.1</c> through a wildcard search
/// domain, where <c>proxy.</c> did not resolve at all. The network check would still refuse such an
/// answer, but there is no reason to ask for it.
/// </para>
/// </remarks>
public sealed class ProxyHostAddresses : IDisposable
{
    /// <summary>How long an answer is believed without asking again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    /// <summary>The least time between two lookups prompted by a peer the last answer did not hold.</summary>
    public static readonly TimeSpan RetryAfterMiss = TimeSpan.FromSeconds(1);

    private readonly string _name;
    private readonly IHostAddressResolver _resolver;
    private readonly TimeProvider _time;
    private readonly ILogger<ProxyHostAddresses> _logger;
    private readonly SemaphoreSlim _lookup = new(1, 1);

    private Answer? _current;

    public ProxyHostAddresses(IOptions<XForwardedOptions> options,
                              IHostAddressResolver resolver,
                              TimeProvider time,
                              ILogger<ProxyHostAddresses> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _name = Absolute(options.Value.ProxyHost);
        _resolver = resolver;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="peer"/> is an address the proxy's name resolves to.
    /// </summary>
    /// <param name="peer">The address the connection came from.</param>
    /// <param name="cancellationToken">Ends the wait for a lookup already running, never the lookup.</param>
    public async Task<bool> IsProxyAsync(IPAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);

        IPAddress address = Normalise(peer);
        Answer? seen = Volatile.Read(ref _current);

        if (seen is not null)
        {
            TimeSpan age = _time.GetElapsedTime(seen.ResolvedAt);

            if (age < Lifetime && (seen.Addresses.Contains(address) || age < RetryAfterMiss))
            {
                return seen.Addresses.Contains(address);
            }
        }

        Answer answer = await LookUpAsync(seen, cancellationToken);
        return answer.Addresses.Contains(address);
    }

    private async Task<Answer> LookUpAsync(Answer? seen, CancellationToken cancellationToken)
    {
        await _lookup.WaitAsync(cancellationToken);

        try
        {
            // Whoever held the lock before us has just asked; their answer is as fresh as ours would be.
            Answer? current = Volatile.Read(ref _current);
            if (current is not null && !ReferenceEquals(current, seen))
            {
                return current;
            }

            IReadOnlyList<IPAddress> resolved = await _resolver.ResolveAsync(_name, CancellationToken.None);
            Answer answer = new([.. resolved.Select(Normalise).Distinct()], _time.GetTimestamp());

            if (current is null || !current.Addresses.SetEquals(answer.Addresses))
            {
                if (answer.Addresses.Count == 0)
                {
                    _logger.LogWarning("The proxy's name {ProxyHost} does not resolve, so forwarded headers are believed from nobody until it does.",
                                       _name);
                }
                else
                {
                    _logger.LogInformation("The proxy's name {ProxyHost} resolves to {ProxyAddresses}; forwarded headers are believed from there alone.",
                                           _name, string.Join(", ", answer.Addresses));
                }
            }

            Volatile.Write(ref _current, answer);
            return answer;
        }
        finally
        {
            _lookup.Release();
        }
    }

    public void Dispose()
    {
        _lookup.Dispose();
    }

    /// <summary>The name with a trailing dot, so no search domain is tried; an address is left alone.</summary>
    private static string Absolute(string name)
    {
        string trimmed = name.Trim();
        bool absolute = trimmed.Length == 0 || trimmed.EndsWith('.') || IPAddress.TryParse(trimmed, out _);
        return absolute ? trimmed : trimmed + ".";
    }

    /// <summary>An IPv4 peer on a dual-mode socket arrives mapped into IPv6; compared as IPv4.</summary>
    private static IPAddress Normalise(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private sealed record Answer(HashSet<IPAddress> Addresses, long ResolvedAt);
}
