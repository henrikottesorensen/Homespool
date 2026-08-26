using System.Collections.Concurrent;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Printing;

/// <summary>
/// Maps a connected printer's id to its live <see cref="IPrinterLink"/>, so a command can
/// be sent from outside the request that accepted the WebSocket upgrade. A directory of actors,
/// nothing more: registered/unregistered by <see cref="PrusaConnect.PrinterConnectionSession"/> for the lifetime
/// of the request that accepted the upgrade.
/// </summary>
public sealed class PrinterConnectionRegistry
{
    private readonly ConcurrentDictionary<int, IPrinterLink> _actors = new();
    private readonly ILogger<PrinterConnectionRegistry> _logger;

    public PrinterConnectionRegistry(ILogger<PrinterConnectionRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Publishes <paramref name="actor"/> as the live connection for <paramref name="printerId"/>,
    /// displacing (and shutting down) any connection already registered under that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Last-wins is deliberate and must stay.</b> A printer whose socket went half-open
    /// reconnects once its own 60s socket timeout fires (Prusa-Firmware-Buddy
    /// <c>connection_cache.cpp</c>), while this server may still be holding the dead one. Refusing
    /// the newcomer would lock the printer out until the stale request reaped - the failure
    /// instance-matched <see cref="Unregister"/> exists to prevent.
    /// </para>
    /// <para>
    /// <b>What changed 2026-07-26, and why it is logged at Error.</b> Displacement used to be a
    /// silent overwrite that left the loser running: its read loop kept consuming telemetry and
    /// persisting it under the same printer id, while <see cref="TryGet"/> returned only the
    /// newcomer - so the displaced connection became a zombie that still wrote history but could
    /// never receive a command, with nothing anywhere saying so. Found when two rig clients
    /// accidentally ran against one identity: both authenticated, both persisted as one printer, and
    /// the merged live state flip-flopped between them.
    /// </para>
    /// <para>
    /// Two clients holding one printer's credentials is either a reconnect (benign, and the common
    /// case) or someone with a copied token impersonating a printer - and <em>the two are
    /// indistinguishable on the wire</em>, because both present a valid fingerprint and token. That
    /// is exactly why this is Error rather than Warning: it is the only signal an operator can ever
    /// get, and the benign case is rare enough (a reconnect after a network fault) to be worth
    /// seeing too. Completing the displaced actor stops the double-write; its mailbox closing ends
    /// its read loop, which reaches the teardown that disposes its socket.
    /// </para>
    /// </remarks>
    public void Register(int printerId, IPrinterLink actor)
    {
        IPrinterLink? displaced = Swap(printerId, actor);

        if (displaced is null)
        {
            return;
        }

        _logger.LogError(
            "[{PrinterId}] a SECOND connection registered while one was already live - the earlier connection has been "
            + "shut down and only the new one will receive commands. Either this printer reconnected after a network "
            + "fault (benign), or something else is presenting its fingerprint and token: both look identical on the "
            + "wire, so if this printer is not reconnecting, treat its credentials as compromised and reissue them.",
            printerId);

        // Ends the loser's read loop (its next post throws into the handler's ordinary exit), which
        // reaches the session teardown that closes the socket.
        displaced.Complete();
    }

    /// <summary>
    /// Conditional (instance-matching) remove. A fast reconnect registers a new actor for the same
    /// <paramref name="printerId"/> before the old request's <c>finally</c> unregisters the old one;
    /// an unconditional remove would delete the new, live actor instead of the stale one. (The race
    /// is between two connection <i>lifetimes</i>, which the actor doesn't change - each connection
    /// still has a request that ends at its own pace.)
    /// </summary>
    public void Unregister(int printerId, IPrinterLink actor)
    {
        _actors.TryRemove(new KeyValuePair<int, IPrinterLink>(printerId, actor));
    }

    public bool TryGet(int printerId, out IPrinterLink? actor)
    {
        return _actors.TryGetValue(printerId, out actor);
    }

    /// <summary>
    /// Shuts down the live connection for <paramref name="printerId"/>, if there is one, because the
    /// printer is being deleted. Returns whether there was one to close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unconditional, unlike <see cref="Unregister"/></b>, and the difference is the caller's
    /// intent rather than an oversight. Unregister matches on the instance because it runs in a
    /// request's <c>finally</c> and must not reap a newer connection that displaced it. Here the
    /// printer itself is going away, so whichever actor holds the id is the one to close - and a
    /// reconnect landing in the same instant should be closed too, not left running.
    /// </para>
    /// <para>
    /// <b>The entry is not removed.</b> <see cref="IPrinterLink.Complete"/>ing the actor ends its read loop, which
    /// reaches the session teardown, which unregisters it by instance - removing it here as well
    /// would take out whatever registered in between, which is exactly what Unregister's
    /// instance-matching exists to prevent.
    /// </para>
    /// </remarks>
    public bool Close(int printerId)
    {
        if (!_actors.TryGetValue(printerId, out IPrinterLink? actor))
        {
            return false;
        }

        _logger.LogInformation("[{PrinterId}] closing the live connection because the printer is being deleted.", printerId);

        actor.Complete();

        return true;
    }

    /// <summary>
    /// Installs <paramref name="actor"/> and returns whatever it replaced, atomically - so two
    /// simultaneous registrations for one printer cannot both report displacing the same actor, and
    /// no actor can be dropped from the map without being handed back to be shut down.
    /// </summary>
    private IPrinterLink? Swap(int printerId, IPrinterLink actor)
    {
        while (true)
        {
            if (_actors.TryGetValue(printerId, out IPrinterLink? existing))
            {
                if (_actors.TryUpdate(printerId, actor, existing))
                {
                    return existing;
                }
            }
            else if (_actors.TryAdd(printerId, actor))
            {
                return null;
            }
        }
    }

    public bool IsConnected(int printerId)
    {
        return _actors.TryGetValue(printerId, out IPrinterLink? actor) && actor.IsOpen;
    }
}
