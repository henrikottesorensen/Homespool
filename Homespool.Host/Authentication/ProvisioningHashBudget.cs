using System;
using System.Threading;

namespace Homespool.Host.Authentication;

/// <summary>
/// How many token hashes the printer port may spend on fingerprints nobody has enrolled - a token
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
/// <b>What an empty bucket costs.</b> A printer making genuine first contact while the bucket is empty
/// is refused as an unknown printer, and firmware retries on its own. So a flood delays enrolment of
/// new USB-key printers - never beyond the eight hours their tokens live - and cannot reach an enrolled
/// printer, whose path never draws from here.
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

    private readonly Lock _lock = new();

    private readonly TimeProvider _time;

    private readonly int _burst;

    private readonly double _perSecond;

    private double _available;

    private DateTimeOffset _refilledAt;

    private long _refusedSinceReport;

    private DateTimeOffset? _reportedAt;

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
