using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
/// set is the honest shape, and its size is the number of challenges issued within one ceremony's
/// lifetime.
/// </para>
/// </remarks>
public sealed class PasskeyCeremonyLedger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outstanding = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a new ceremony that may be answered until <paramref name="expires"/>, and returns its
    /// id. Ceremonies already past their time at <paramref name="now"/> are forgotten on the way.
    /// </summary>
    public string Begin(DateTimeOffset now, DateTimeOffset expires)
    {
        foreach (KeyValuePair<string, DateTimeOffset> entry in _outstanding)
        {
            if (entry.Value <= now)
            {
                _outstanding.TryRemove(entry.Key, out _);
            }
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
}
