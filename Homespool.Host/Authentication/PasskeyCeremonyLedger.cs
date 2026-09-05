using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Homespool.Host.Authentication;

/// <summary>
/// Which passkey ceremonies this server has issued and not yet seen answered. A ceremony is spent the
/// moment its answer is read, so a second answer - the same cookie and the same assertion presented
/// again - is refused whatever the credential says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cookie alone is not enough.</b> The ceremony state rides in a data-protected cookie the
/// handler deletes on first use, but deletion is an instruction to the browser, and a copy of the
/// request taken before it is a complete answer that still verifies: the challenge matches, the
/// signature is good, and most platform authenticators report a sign count of zero for ever, which
/// takes the one check that would notice a repeat out of play. So the server keeps its own memory of
/// what it issued, and the state's ceremony id must still be in it.
/// </para>
/// <para>
/// <b>Issued, not spent.</b> Remembering the answered ones would let a replay through after a restart
/// emptied the list; remembering the outstanding ones means a restart refuses every ceremony in
/// flight instead, and somebody presses the button again. On a single-process appliance an in-memory
/// set is the honest shape.
/// </para>
/// <para>
/// <b>Bounded, because the challenge is anonymous.</b> The login page hands one out to anybody with
/// an antiforgery pair, and a page load supplies that, so the list would otherwise grow at whatever
/// rate somebody cared to ask for five minutes at a time. Expired entries are swept every
/// <see cref="SweepInterval"/> begins rather than on each one, and above <see cref="MaxOutstanding"/>
/// live entries a new ceremony is refused - the caller answers 503, and a person presses the button
/// again a minute later, which is the right failure for a box with a fixed amount of memory.
/// </para>
/// </remarks>
public sealed class PasskeyCeremonyLedger
{
    /// <summary>
    /// The most ceremonies that may be outstanding at once. Tens of people on a household appliance
    /// start a handful an hour; this is a ceiling on abuse, not on use.
    /// </summary>
    public const int MaxOutstanding = 4096;

    /// <summary>How many begins pass between sweeps of expired entries.</summary>
    public const int SweepInterval = 64;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _outstanding = new(StringComparer.Ordinal);

    private int _beginsSinceSweep;

    /// <summary>
    /// Records a new ceremony that may be answered until <paramref name="expires"/>, and returns its
    /// id - or <see langword="null"/> when the ledger is full of ceremonies that have not yet expired
    /// at <paramref name="now"/>.
    /// </summary>
    public string? Begin(DateTimeOffset now, DateTimeOffset expires)
    {
        if (Interlocked.Increment(ref _beginsSinceSweep) >= SweepInterval || _outstanding.Count >= MaxOutstanding)
        {
            Sweep(now);
        }

        if (_outstanding.Count >= MaxOutstanding)
        {
            return null;
        }

        string id = Guid.NewGuid().ToString("N");
        _outstanding[id] = expires;

        return id;
    }

    /// <summary>
    /// Spends the ceremony <paramref name="id"/>: <see langword="true"/> exactly once for an id this
    /// server issued and has not seen answered, and never again.
    /// </summary>
    public bool TrySpend(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _outstanding.TryRemove(id, out _);
    }

    /// <summary>How many ceremonies are outstanding, for a test to watch the list stay small.</summary>
    public int Outstanding => _outstanding.Count;

    private void Sweep(DateTimeOffset now)
    {
        Interlocked.Exchange(ref _beginsSinceSweep, 0);

        foreach (KeyValuePair<string, DateTimeOffset> entry in _outstanding)
        {
            if (entry.Value <= now)
            {
                _outstanding.TryRemove(entry.Key, out _);
            }
        }
    }
}
