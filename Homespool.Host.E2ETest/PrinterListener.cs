using System;
using System.Net.Http;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Controllers;
using Homespool.Host.Listeners;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Addresses the printer listener, for the tests that are pretending to be a printer.
/// </summary>
/// <remarks>
/// <para>
/// <c>/p/*</c> exists only on that listener, so a test that reaches it through an ordinary
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> gets a 404 - correctly, and the
/// same 404 a real printer dialling the user's port would get. These helpers are how a test says
/// which listener it means.
/// </para>
/// <para>
/// The port is read from the running application's own options rather than written down here, so the
/// tests cannot drift from the default they are exercising.
/// </para>
/// </remarks>
internal static class PrinterListener
{
    /// <summary>The port the application has bound for printers.</summary>
    public static int PortOf(WebApplicationFactory<PrinterAppController> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.Services.GetRequiredService<IOptions<ListenerOptions>>().Value.PrinterPort;
    }

    /// <summary>A client whose requests arrive on the printer listener.</summary>
    public static HttpClient CreateClient(WebApplicationFactory<PrinterAppController> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"http://localhost:{PortOf(factory)}"),
        });
    }

    /// <summary>The <c>/p/ws</c> address on the printer listener.</summary>
    public static Uri WebSocketUri(WebApplicationFactory<PrinterAppController> factory)
    {
        return new($"ws://localhost:{PortOf(factory)}/p/ws");
    }
}
