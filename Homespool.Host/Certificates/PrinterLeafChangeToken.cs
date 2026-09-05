using System.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Homespool.Host.Certificates;

/// <summary>
/// Fires whenever the printer leaf is issued, for anything that derived a value from its names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raised by the authority, not by the callers that asked it to issue.</b> Three paths issue a
/// leaf — the first run, the half-pair repair and the administrator's reissue — and a fourth would be
/// written by somebody who had not read the other three. A consumer that caches the leaf's names and
/// misses one issuance keeps refusing a name the certificate already vouches for until the next
/// restart, which is the failure this exists to close.
/// </para>
/// <para>
/// One consumer today: the host filter, which allows exactly the names on the leaf
/// (<see cref="Listeners.PrinterHostFiltering"/>). Shaped as a change token because that is what the
/// options system consumes, so the consumer needs no cache and no subscription of its own.
/// </para>
/// <para>
/// <see cref="ConfigurationReloadToken"/> is borrowed for the mechanics rather than reimplemented:
/// it is the framework's own swap-and-fire token, and it owns nothing that needs disposing — a
/// cancellation source held here directly would make every test that constructs the authority
/// responsible for one.
/// </para>
/// </remarks>
public sealed class PrinterLeafChangeToken
{
    private ConfigurationReloadToken _token = new();

    /// <summary>A token that fires at the next issuance.</summary>
    public IChangeToken GetChangeToken()
    {
        return _token;
    }

    /// <summary>Fires every token handed out since the last call.</summary>
    public void NotifyIssued()
    {
        ConfigurationReloadToken previous = Interlocked.Exchange(ref _token, new ConfigurationReloadToken());

        previous.OnReload();
    }
}
