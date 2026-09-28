using System;
using System.Collections.Concurrent;

using Lib.Net.Http.WebPush.Authentication;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Keeps each push service's signed VAPID token until shortly before it expires, so one is signed per
/// service every few hours rather than one per notification.
/// </summary>
/// <remarks>
/// Worth having for more than the signature's cost: without a cache the library signs every request
/// with one shared <c>ECDsa</c>, and nothing promises that instance may be used from two threads at
/// once. With it, signing happens rarely enough that two deliveries meeting inside it is unlikely -
/// and a clash costs one more signature, since a token is valid whoever wrote it last.
/// </remarks>
public sealed class VapidTokenCache : IVapidTokenCache
{
    /// <summary>
    /// How long before a token's own expiry it stops being handed out. A push service compares
    /// <c>exp</c> against its own clock, and a request can sit in a retry for a while.
    /// </summary>
    private static readonly TimeSpan Margin = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    public VapidTokenCache(TimeProvider time)
    {
        _time = time;
    }

    public void Put(string audience, DateTimeOffset expiration, string token)
    {
        _tokens[audience] = new Entry(expiration, token);
    }

    public string? Get(string audience)
    {
        if (_tokens.TryGetValue(audience, out Entry entry) &&
            entry.Expiration - Margin > _time.GetUtcNow())
        {
            return entry.Token;
        }

        return null;
    }

    /// <summary>A signed token and when it stops being accepted.</summary>
    private readonly record struct Entry(DateTimeOffset Expiration, string Token);
}
