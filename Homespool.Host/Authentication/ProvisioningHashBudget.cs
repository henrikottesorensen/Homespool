using System;
using System.Collections.Generic;
using System.Threading;

namespace Homespool.Host.Authentication;

/// <summary>
/// How many token hashes the printer port may spend on callers who have proved nothing - a token
/// bucket counted in hashes rather than in requests, shared by every caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why requests were the wrong unit.</b> A request carrying an unknown fingerprint is checked
/// against every live USB-key provisioning token, one PBKDF2 each, before anything is authenticated.
/// The route limits bound how many such requests arrive, but any account can mint provisioning rows,
/// so the number of hashes behind each request is the caller's to choose. At 22 ms a hash on a
/// Raspberry Pi 3, eight live rows at the route ceilings keep all four cores busy. Counting the hashes
/// themselves bounds the work whatever the row count and whatever the caller's addresses look like.
/// </para>
/// <para>
/// <b>A known fingerprint is no proof either.</b> It is an identifier the printer sends in clear, so
/// anyone who has seen one can present it with a made-up token, and each such request costs a PBKDF2
/// against the enrolled credential - two when a reissued token is outstanding for that printer. The
/// per-printer windows admit a few of those a second for every fingerprint known, which on their own
/// would outspend this bucket several times over. So those hashes are drawn from here too, through
/// <see cref="TryTakeFor"/>.
/// </para>
/// <para>
/// <b>But an enrolled printer's own hash never waits on strangers.</b> Its token is remembered once it
/// verifies (<see cref="VerifiedPrinterTokens"/>), so the only full verification it needs is one a
/// <see cref="VerifiedPrinterTokens.Lifetime"/>, when the memo lapses. <see cref="TryTakeFor"/> grants
/// exactly that much per stored credential without touching the bucket, and draws everything past it.
/// A flood of invented fingerprints therefore cannot keep an enrolled printer out; a caller presenting
/// that printer's own fingerprint can spend its free hash first, which is no more than it can already
/// do by spending the printer's route window as the printer.
/// </para>
/// <para>
/// <b>What an empty bucket costs.</b> A printer making genuine first contact while the bucket is empty
/// is refused as an unknown printer, and firmware retries on its own. So a flood delays enrolment of
/// new USB-key printers - never beyond the eight hours their tokens live - and a reissued token being
/// rebound the same way. It cannot reach an enrolled printer presenting the token it enrolled with.
/// </para>
/// <para>
/// <b>Refilled lazily from the clock</b>, on the next take rather than by a timer, so a test can move
/// time and nothing runs while the port is quiet.
/// </para>
/// </remarks>
public sealed class ProvisioningHashBudget
{
    /// <summary>
    /// Hashes the bucket regains a second. On a Raspberry Pi 3, the slowest supported board, that is at
    /// most about a fifth of one core spent on strangers.
    /// </summary>
    public const double HashesPerSecond = 10;

    /// <summary>
    /// The most hashes available at once: ten printers making first contact together against ten live
    /// tokens, each checking half of them on average.
    /// </summary>
    public const int Burst = 50;

    /// <summary>How often an empty bucket is reported, at most.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often each enrolled credential gets a hash without drawing from the bucket: once a memo
    /// lifetime, which is how often its printer has to prove itself in full.
    /// </summary>
    public static readonly TimeSpan CredentialInterval = VerifiedPrinterTokens.Lifetime;

    private readonly Lock _lock = new();

    private readonly TimeProvider _time;

    private readonly int _burst;

    private readonly double _perSecond;

    /// <summary>
    /// When each stored credential last had its free hash. Keyed by the stored hash, which only the
    /// database supplies, so it holds no more than the credentials enrolled within an interval;
    /// lapsed entries are swept once an interval.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _credentialHashedAt = new(StringComparer.Ordinal);

    private double _available;

    private DateTimeOffset _refilledAt;

    private long _refusedSinceReport;

    private DateTimeOffset? _reportedAt;

    private DateTimeOffset _nextSweep;

    public ProvisioningHashBudget(TimeProvider time, int burst = Burst, double perSecond = HashesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);

        _time = time;
        _burst = burst;
        _perSecond = perSecond;
        _available = burst;
        _refilledAt = time.GetUtcNow();
        _nextSweep = _refilledAt + CredentialInterval;
    }

    /// <summary>Whole hashes available now.</summary>
    public int Available
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return (int)Math.Floor(_available);
            }
        }
    }

    /// <summary>
    /// Takes one hash from the bucket. <c>false</c> means none is left and the hash must not be run.
    /// </summary>
    public bool TryTake()
    {
        lock (_lock)
        {
            Refill();

            if (_available < 1)
            {
                _refusedSinceReport++;
                return false;
            }

            _available--;
            return true;
        }
    }

    /// <summary>
    /// Takes one hash for verifying a token against the enrolled credential stored as
    /// <paramref name="storedHash"/>: free once a <see cref="CredentialInterval"/> for that credential,
    /// from the bucket otherwise. <c>false</c> means neither is left and the hash must not be run.
    /// </summary>
    public bool TryTakeFor(string storedHash)
    {
        ArgumentNullException.ThrowIfNull(storedHash);

        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();

            SweepIfDue(now);

            if (!_credentialHashedAt.TryGetValue(storedHash, out DateTimeOffset last) || now - last >= CredentialInterval)
            {
                _credentialHashedAt[storedHash] = now;
                return true;
            }
        }

        return TryTake();
    }

    /// <summary>
    /// How many takes were refused since the last report, when an empty bucket should be reported now;
    /// otherwise <c>null</c>. At most once a <see cref="ReportInterval"/>, so a flood costs one log line
    /// a minute rather than one per request.
    /// </summary>
    public long? TakeReport()
    {
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();

            if (_refusedSinceReport == 0 || (_reportedAt is { } last && now - last < ReportInterval))
            {
                return null;
            }

            long refused = _refusedSinceReport;
            _refusedSinceReport = 0;
            _reportedAt = now;
            return refused;
        }
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now + CredentialInterval;

        foreach (KeyValuePair<string, DateTimeOffset> entry in _credentialHashedAt)
        {
            if (now - entry.Value >= CredentialInterval)
            {
                _credentialHashedAt.Remove(entry.Key);
            }
        }
    }

    private void Refill()
    {
        DateTimeOffset now = _time.GetUtcNow();
        double seconds = (now - _refilledAt).TotalSeconds;

        if (seconds > 0)
        {
            _available = Math.Min(_burst, _available + (seconds * _perSecond));
            _refilledAt = now;
        }
    }
}
