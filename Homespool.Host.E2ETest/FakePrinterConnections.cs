using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.Controllers;
using Homespool.Host.Printing;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Connecting a <see cref="FakePrinterClient"/> through the test server, and waiting for the things
/// a connection makes true - for the dispatch tests, which all need a live socket to prove either
/// that a command arrived or that nothing did.
/// </summary>
public static class FakePrinterConnections
{
    /// <summary>
    /// A connector that opens the fake's WebSocket against <paramref name="factory"/>'s printer
    /// listener, carrying the headers the fake asks for.
    /// </summary>
    public static WebSocketConnector ViaTestServerAsync(WebApplicationFactory<PrinterAppController> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return async (request, cancellationToken) =>
        {
            WebSocketClient client = factory.Server.CreateWebSocketClient();
            client.SubProtocols.Add(request.SubProtocol);
            client.ConfigureRequest = httpRequest =>
            {
                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    httpRequest.Headers[header.Key] = header.Value;
                }
            };

            return await client.ConnectAsync(PrinterListener.WebSocketUri(factory), cancellationToken);
        };
    }

    /// <summary>
    /// Waits for the printer to appear in the connection registry, which is when the command path
    /// can reach it.
    /// </summary>
    public static async Task WaitUntilConnectedAsync(WebApplicationFactory<PrinterAppController> factory, int printerId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        PrinterConnectionRegistry registry = factory.Services.GetRequiredService<PrinterConnectionRegistry>();

        bool connected = await WaitUntilAsync(() => registry.IsConnected(printerId), TimeSpan.FromSeconds(10));

        if (!connected)
        {
            throw new TimeoutException($"Printer {printerId} never appeared in the connection registry.");
        }
    }

    /// <summary>
    /// Polls a predicate until it holds - the two ends talk to each other on their own schedule, so
    /// there is no single await a test could hold instead.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
