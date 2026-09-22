using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Authentication;

/// <summary>
/// Printer tokens this process has already seen verify against a stored hash, so an enrolled printer
/// pays the PBKDF2 once per <see cref="Lifetime"/> rather than on every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>A memo of a fact that cannot go stale, not a cache of a credential.</b> What is remembered is
/// "this token verifies against this stored hash", keyed by the stored hash string itself. That is a
/// property of the two values alone - <see cref="TokenService.VerifyToken"/> reads nothing else - so it
/// stays true for as long as the entry lives. Whether that hash is still a printer's credential is a
/// separate question, and the handler still asks the database it on every request: removing a printer
/// deletes the row the lookup needs, and every credential change writes a freshly salted hash, which is
/// a key nothing here holds. Revocation therefore needs no eviction and takes effect on the next
/// request.
/// </para>
/// <para>
/// <b>Why it is worth having.</b> The HTTP transport authenticates every telemetry and event post, one
/// to a few a second per printer, and a hash costs 22 ms on a Raspberry Pi 3 - a few percent of a core
/// per printer, spent re-proving something already proved. The socket authenticates once per upgrade
/// and barely notices either way.
/// </para>
/// <para>
/// <b>A wrong token costs exactly what it did.</b> A miss falls through to the full verification
/// whether or not an entry exists for that hash, so the answer to a wrong token takes as long as it
/// always has and says nothing about whether the printer has been heard from lately. Nothing is
/// remembered for a failure: a failed guess is not worth a row, and only a verified token can add one,
/// so a caller without a valid token cannot grow this.
/// </para>
/// <para>
/// <b>What is held is a SHA-256 of the token, not the token.</b> The token is 120 bits from a CSPRNG, so
/// an unsalted fast digest of it is as far from the token as the stored PBKDF2 is; the digest is what
/// makes a memory dump no more useful than the database already is.
/// </para>
/// <para>
/// <b>Bounded by what has verified.</b> One entry per credential presented within a lifetime - in
/// practice one per enrolled printer, plus the superseded one for a lifetime after a rebind. Expired
/// entries are swept once a <see cref="Lifetime"/>, on the way into <see cref="Remember"/>.
/// </para>
/// </remarks>
public sealed class VerifiedPrinterTokens
{
    /// <summary>
    /// How long a verification is trusted before the next request pays for it again. Correctness does
    /// not depend on it, since the database decides what is still a credential; it bounds how long a
    /// superseded hash's entry lingers and how long a token's digest stays in memory.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Verified> _verified = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    private long _nextSweepTicks;

    public VerifiedPrinterTokens(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _nextSweepTicks = (time.GetUtcNow() + Lifetime).UtcTicks;
    }

    /// <summary>How many verifications are held, expired or not yet swept.</summary>
    public int Count => _verified.Count;

    /// <summary>
    /// Whether <paramref name="token"/> has verified against <paramref name="storedHash"/> within the
    /// last <see cref="Lifetime"/>. <c>false</c> means "not known", never "wrong": the caller verifies
    /// in full.
    /// </summary>
    public bool IsVerified(string storedHash, string token)
    {
        ArgumentNullException.ThrowIfNull(storedHash);
        ArgumentNullException.ThrowIfNull(token);

        if (!_verified.TryGetValue(storedHash, out Verified? entry))
        {
            return false;
        }

        if (_time.GetUtcNow() >= entry.ExpiresAt)
        {
            // Removed only if it is still this entry, so a concurrent Remember that has just replaced
            // it with a fresh one is not undone.
            _verified.TryRemove(new KeyValuePair<string, Verified>(storedHash, entry));
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Digest(token), entry.TokenDigest);
    }

    /// <summary>
    /// Records that <paramref name="token"/> has just verified against <paramref name="storedHash"/>.
    /// Call only after <see cref="TokenService.VerifyToken"/> returned <c>true</c> for exactly this pair.
    /// </summary>
    public void Remember(string storedHash, string token)
    {
        ArgumentNullException.ThrowIfNull(storedHash);
        ArgumentNullException.ThrowIfNull(token);

        DateTimeOffset now = _time.GetUtcNow();

        _verified[storedHash] = new Verified(Digest(token), now + Lifetime);

        SweepIfDue(now);
    }

    private static byte[] Digest(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        long due = Interlocked.Read(ref _nextSweepTicks);

        // One caller wins the sweep; the rest see the moved deadline and carry on.
        if (now.UtcTicks < due ||
            Interlocked.CompareExchange(ref _nextSweepTicks, (now + Lifetime).UtcTicks, due) != due)
        {
            return;
        }

        foreach (KeyValuePair<string, Verified> entry in _verified)
        {
            if (now >= entry.Value.ExpiresAt)
            {
                _verified.TryRemove(entry);
            }
        }
    }

    private sealed record Verified(byte[] TokenDigest, DateTimeOffset ExpiresAt);
}
