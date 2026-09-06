using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Homespool.Host.Authentication;

/// <summary>
/// Which passkey ceremonies this server has seen answered. A ceremony is spent when its answer
/// verifies, so a second answer - the same cookie and the same assertion presented again - is refused
/// whatever the credential says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cookie alone is not enough.</b> The ceremony state rides in a data-protected cookie the
/// handler deletes on first use, but deletion is an instruction to the browser, and a copy of the
/// request taken before it is a complete answer that still verifies: the challenge matches, the
/// signature is good, and most platform authenticators report a sign count of zero for ever, which
/// takes the one check that would notice a repeat out of play. So the server keeps its own memory of
/// what was answered, and the state's ceremony id must not be in it.
/// </para>
/// <para>
/// <b>Spent, not issued - since 2026-09-06.</b> The first ledger remembered every ceremony issued and
/// forgot it when answered, which meant an anonymous challenge bought a row of server state: the
/// login page hands one out to anybody with an antiforgery pair, so fourteen requests a second kept
/// the cap full and every passkey sign-in at 503. Remembering the answered ones instead costs a
/// challenge nothing; a row here takes a signed assertion or attestation that verified, and a
/// failed answer needs no row because replaying it fails again.
/// </para>
/// <para>
/// <b>A restart forgets, and refuses what it cannot remember.</b> A spent set emptied by a restart
/// would let an answer captured before it through; so a ceremony issued before this process started
/// is refused outright, the same outcome the old ledger had for every ceremony in flight, and the
/// person presses the button again.
/// </para>
/// <para>
/// <b>Bounded, still.</b> Expired entries are swept every <see cref="SweepInterval"/> spends rather
/// than on each one, and above <see cref="MaxSpent"/> live entries a further spend is refused - a
/// ceiling that now takes real verified answers to reach, and is refused rather than evicted because
/// an evicted entry is a replay window.
/// </para>
/// </remarks>
public sealed class PasskeyCeremonyLedger
{
    /// <summary>
    /// The most answered ceremonies remembered at once. Tens of people on a household appliance
    /// answer a handful an hour; this is a ceiling on abuse, not on use.
    /// </summary>
    public const int MaxSpent = 4096;

    /// <summary>How many spends pass between sweeps of expired entries.</summary>
    public const int SweepInterval = 64;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _spent = new(StringComparer.Ordinal);

    private int _spendsSinceSweep;

    public PasskeyCeremonyLedger(TimeProvider? time = null)
    {
        // To the second, rounded down: a ceremony's issue time rides the cookie in RFC 1123 form,
        // which keeps whole seconds, and a ceremony issued in the same second this process started
        // must not read as issued before it.
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        StartedUtc = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), now.Offset);
    }

    /// <summary>What a spend came to.</summary>
    public enum SpendResult
    {
        /// <summary>Recorded; the ceremony is answered and cannot be again.</summary>
        Spent = 0,

        /// <summary>Already recorded: this is a replay.</summary>
        AlreadySpent = 1,

        /// <summary>Issued before this process started, so whether it was answered is unknowable.</summary>
        BeforeThisProcess = 2,

        /// <summary>The ledger holds its maximum of live entries.</summary>
        Full = 3,
    }

    /// <summary>When this ledger began remembering; a ceremony issued earlier is refused.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>How many answered ceremonies are remembered, expired ones included until the next sweep.</summary>
    public int Spent => _spent.Count;

    /// <summary>Whether <paramref name="id"/> is known to have been answered, or predates this process.</summary>
    public bool IsSpent(string id, DateTimeOffset issued)
    {
        ArgumentNullException.ThrowIfNull(id);

        return issued < StartedUtc || _spent.ContainsKey(id);
    }

    /// <summary>
    /// Records that the ceremony <paramref name="id"/>, issued at <paramref name="issued"/> and good
    /// until <paramref name="expires"/>, has been answered.
    /// </summary>
    public SpendResult Spend(string id, DateTimeOffset issued, DateTimeOffset expires, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (issued < StartedUtc)
        {
            return SpendResult.BeforeThisProcess;
        }

        if (Interlocked.Increment(ref _spendsSinceSweep) >= SweepInterval || _spent.Count >= MaxSpent)
        {
            Sweep(now);
        }

        if (_spent.Count >= MaxSpent)
        {
            return SpendResult.Full;
        }

        return _spent.TryAdd(id, expires) ? SpendResult.Spent : SpendResult.AlreadySpent;
    }

    private void Sweep(DateTimeOffset now)
    {
        Interlocked.Exchange(ref _spendsSinceSweep, 0);

        foreach (KeyValuePair<string, DateTimeOffset> entry in _spent)
        {
            if (entry.Value <= now)
            {
                _spent.TryRemove(entry.Key, out _);
            }
        }
    }
}
