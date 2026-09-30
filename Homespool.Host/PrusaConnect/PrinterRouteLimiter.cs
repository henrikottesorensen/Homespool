using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer routes' limiter: a window per caller beneath a ceiling on each route's total, spent
/// together or not at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refused request costs nothing, and that is the whole reason this is not two framework
/// limiters.</b> Built as the ceiling in the global slot and the caller's window as the endpoint
/// policy, a request the window refused had already taken a ceiling permit, which a fixed window
/// never gives back - and the middleware then asks both again before giving up, taking a second.
/// Reproduced against this application: one fingerprint sending 70 socket upgrades (window 20,
/// ceiling 120) spent the whole ceiling, 20 + 50 x 2, and the next printer was refused. Here both
/// counts are checked first and both are spent only when both have room, so one caller over its own
/// window leaves the ceiling to everybody else, and the middleware's second attempt repeats a check
/// that changes nothing.
/// </para>
/// <para>
/// <b>What it cannot stop is a caller rotating keys.</b> A fingerprint is the caller's to invent, so
/// each new one arrives with a full window and spends the ceiling; that is what the ceiling is for,
/// and telling an enrolled printer from an invented one needs identity this runs too early to have.
/// An address is not cheap to rotate, which is what makes the per-address window worth having.
/// </para>
/// <para>
/// <b>Bounded at <see cref="PartitionCapacity"/> callers per route, least recently seen evicted
/// first.</b> A window is only created when the ceiling has room, so a flood of invented keys can
/// create no more in a minute than the ceiling admits - but the telemetry ceiling admits more than
/// the table holds, so the table needs its own bound. Evicting a caller only forgets its count, which
/// hands it a fresh window: an attacker can evict a printer's window and thereby help it, never hurt
/// it.
/// </para>
/// <para>
/// <b>A window starts at a caller's first request and runs for <see cref="PrinterRateLimits.Window"/></b>,
/// counted lazily from the clock rather than by a timer, as <c>ProvisioningHashBudget</c> is, so
/// nothing runs while the port is quiet and a test can move time.
/// </para>
/// </remarks>
public sealed class PrinterRouteLimiter : PartitionedRateLimiter<HttpContext>
{
    /// <summary>
    /// How many callers each route remembers. Two orders of magnitude above the tens of printers a
    /// deployment has, and a few hundred kilobytes at most across every route.
    /// </summary>
    public const int PartitionCapacity = 1024;

    private static readonly RateLimitLease Admitted = new Lease(true);

    private static readonly RateLimitLease Refused = new Lease(false);

    private readonly Lock _lock = new();

    private readonly TimeProvider _time;

    private readonly int _capacity;

    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);

    public PrinterRouteLimiter(TimeProvider time, int capacity = PartitionCapacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _time = time;
        _capacity = capacity;
    }

    /// <inheritdoc/>
    public override RateLimiterStatistics? GetStatistics(HttpContext resource)
    {
        return null;
    }

    /// <inheritdoc/>
    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Outside the lock: it reads the endpoint, the headers and the bound options, none of which
        // this class guards.
        PrinterRateLimits.Demand? demand = PrinterRateLimits.DemandOf(resource);

        if (demand is not { } wanted)
        {
            return Admitted;
        }

        lock (_lock)
        {
            if (!_routes.TryGetValue(wanted.Policy, out Route? route))
            {
                route = new Route();
                _routes.Add(wanted.Policy, route);
            }

            return route.TryAdmit(wanted, permitCount, _time.GetUtcNow(), _capacity) ? Admitted : Refused;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing queues: a refusal is immediate, which is what the firmware's retry loops expect.
    /// </remarks>
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(AttemptAcquireCore(resource, permitCount));
    }

    /// <summary>One window's count, restarted once <see cref="PrinterRateLimits.Window"/> has passed.</summary>
    private sealed class Window
    {
        public DateTimeOffset StartedAt { get; private set; }

        public int Spent { get; private set; }

        public void Roll(DateTimeOffset now)
        {
            if (now - StartedAt >= PrinterRateLimits.Window)
            {
                StartedAt = now;
                Spent = 0;
            }
        }

        public bool HasRoom(int permits, int limit)
        {
            return Spent + permits <= limit;
        }

        public void Spend(int permits)
        {
            Spent += permits;
        }
    }

    /// <summary>One route's ceiling, and the windows of the callers it has seen most recently.</summary>
    private sealed class Route
    {
        private readonly Window _ceiling = new();

        private readonly Dictionary<string, LinkedListNode<Caller>> _callers = new(StringComparer.Ordinal);

        private readonly LinkedList<Caller> _recency = new();

        public bool TryAdmit(PrinterRateLimits.Demand demand, int permits, DateTimeOffset now, int capacity)
        {
            _ceiling.Roll(now);

            if (!_ceiling.HasRoom(permits, demand.Ceiling))
            {
                return false;
            }

            if (demand is not { Caller: { } key, PerCaller: { } limit })
            {
                _ceiling.Spend(permits);
                return true;
            }

            Window window = WindowOf(key, now, capacity);
            window.Roll(now);

            if (!window.HasRoom(permits, limit))
            {
                return false;
            }

            window.Spend(permits);
            _ceiling.Spend(permits);
            return true;
        }

        /// <summary>
        /// The caller's window, created if it has none, and marked as the most recently seen.
        /// </summary>
        private Window WindowOf(string key, DateTimeOffset now, int capacity)
        {
            if (_callers.TryGetValue(key, out LinkedListNode<Caller>? node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Window;
            }

            if (_callers.Count >= capacity && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _callers.Remove(oldest.Value.Key);
            }

            Window window = new();
            window.Roll(now);

            _callers.Add(key, _recency.AddFirst(new Caller(key, window)));
            return window;
        }
    }

    /// <summary>A caller's key and its window, as one entry in a route's recency list.</summary>
    private sealed record Caller(string Key, Window Window);

    /// <summary>A lease with nothing to give back: permits here are counted, not held.</summary>
    private sealed class Lease(bool acquired) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
