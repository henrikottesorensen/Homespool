using System;
using System.Collections.Generic;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Records every call to <see cref="MessageDispatcher.Classify"/> instead of acting on it, so a test
/// can assert directly on what reached the dispatcher - the printer id the auth handler resolved, and
/// the parsed message - rather than scraping console output.
/// </summary>
/// <remarks>
/// Replaces the production behaviour entirely rather than wrapping it: deserialization is already
/// covered by <c>MessageDispatcherTests</c> and reassembly by <c>WebSocketHandlerParsingTests</c>.
/// This spy exists solely to answer "did the real handler chain thread the right printer id all the
/// way through", which is what <c>PrusaConnectWebSocketTests</c> needs and nothing lower-level can
/// prove. Returning null makes the handler post nothing to the connection's actor, keeping the spy
/// free of persistence side effects.
/// </remarks>
internal sealed class CapturingMessageDispatcher : MessageDispatcher
{
    public CapturingMessageDispatcher()
        : base(NullLogger<MessageDispatcher>.Instance,
               new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
               TimeProvider.System)
    {
    }

    public List<(int printerId, JsonElement root)> Calls { get; } = [];

    public override ConnectionMessage? Classify(int printerId, JsonElement root)
    {
        // Cloned: WebSocketHandler disposes the JsonDocument backing root immediately after this call
        // returns, so a test inspecting Calls afterward would otherwise hit ObjectDisposedException.
        Calls.Add((printerId, root.Clone()));

        return null;
    }
}
