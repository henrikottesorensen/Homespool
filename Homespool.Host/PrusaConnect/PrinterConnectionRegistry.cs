using System.Collections.Concurrent;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Maps a connected printer's id to its live <see cref="IPrinterConnectionActor"/>, so a command can
/// be sent from outside the request that accepted the WebSocket upgrade. A directory of actors,
/// nothing more: registered/unregistered by <see cref="PrinterConnectionSession"/> for the lifetime
/// of the request that accepted the upgrade.
/// </summary>
public sealed class PrinterConnectionRegistry
{
    private readonly ConcurrentDictionary<int, IPrinterConnectionActor> _actors = new();
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
    public void Register(int printerId, IPrinterConnectionActor actor)
    {
        IPrinterConnectionActor? displaced = Swap(printerId, actor);

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
    public void Unregister(int printerId, IPrinterConnectionActor actor)
    {
        _actors.TryRemove(new KeyValuePair<int, IPrinterConnectionActor>(printerId, actor));
    }

    public bool TryGet(int printerId, out IPrinterConnectionActor? actor)
    {
        return _actors.TryGetValue(printerId, out actor);
    }

    /// <summary>
    /// Installs <paramref name="actor"/> and returns whatever it replaced, atomically - so two
    /// simultaneous registrations for one printer cannot both report displacing the same actor, and
    /// no actor can be dropped from the map without being handed back to be shut down.
    /// </summary>
    private IPrinterConnectionActor? Swap(int printerId, IPrinterConnectionActor actor)
    {
        while (true)
        {
            if (_actors.TryGetValue(printerId, out IPrinterConnectionActor? existing))
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
        return _actors.TryGetValue(printerId, out IPrinterConnectionActor? actor) && actor.IsOpen;
    }
}
