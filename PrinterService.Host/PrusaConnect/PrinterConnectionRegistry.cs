using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Maps a connected printer's id to its live <see cref="IPrinterConnectionActor"/>, so a command can
/// be sent from outside the request that accepted the WebSocket upgrade. A directory of actors,
/// nothing more: registered/unregistered by <see cref="PrinterConnectionSession"/> for the lifetime
/// of the request that accepted the upgrade.
/// </summary>
public sealed class PrinterConnectionRegistry
{
    private readonly ConcurrentDictionary<int, IPrinterConnectionActor> _actors = new();

    public void Register(int printerId, IPrinterConnectionActor actor)
    {
        _actors[printerId] = actor;
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

    public bool TryGet(int printerId, out IPrinterConnectionActor? actor) => _actors.TryGetValue(printerId, out actor);

    public bool IsConnected(int printerId) => _actors.TryGetValue(printerId, out IPrinterConnectionActor? actor) && actor.IsOpen;
}
