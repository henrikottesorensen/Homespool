using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Listeners;

/// <summary>
/// Wires up the listeners, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
/// <remarks>
/// <c>Cameras/Registration.cs</c> is the same idea: the configuration for a thing lives beside the
/// thing.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Binds the listeners — plain HTTP for people, plain HTTP for printers — and the classification
    /// middleware that keeps each set of routes on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configuring any endpoint here means configuring all of them.</b> Kestrel ignores
    /// <c>ASPNETCORE_URLS</c> / <c>applicationUrl</c> entirely once endpoints are set in code — it
    /// logs "Overriding address(es)" and binds these instead — so the user listener is named here too
    /// rather than left to the environment. <c>Listeners:UserPort</c> defaults to the 8080 the base
    /// image already used, so a deployment that sets nothing keeps the port it had.
    /// </para>
    /// <para>
    /// <b>Kestrel no longer terminates TLS for printers, and that reverses a recorded decision.</b>
    /// It used to mint the leaf here and serve it on this very listener; it does neither, because
    /// <see cref="System.Net.Security.SslStream"/> ignores the RFC 6066 <c>max_fragment_length</c> a
    /// printer negotiates and OpenSSL honours it. A printer holds 1024 bytes of TLS plaintext at a
    /// time, so a record larger than that kills every file transfer — which is what shipped, until
    /// nginx was moved in front of this listener too. The leaf is still ours:
    /// <see cref="Certificates.PrinterCertificateStartup.EnsurePrinterCertificate"/> mints it
    /// on the startup path, and nginx presents it.
    /// </para>
    /// <para>
    /// <b>The split outlived the certificate that motivated it, deliberately.</b> With TLS gone from
    /// both listeners they are two plain HTTP ports, and one would do — except that the boundary is
    /// the point. <c>/p/*</c> exists on the printer listener alone, enforced by
    /// <see cref="Microsoft.AspNetCore.Http.ConnectionInfo.LocalPort"/>; collapsing them would leave a
    /// line of nginx configuration as the only thing between a proxied user request and the printer
    /// protocol, turning a structural guarantee into a configuration one. Two ports cost four lines.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddHomespoolListeners(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<ListenerOptions>(
            builder.Configuration.GetSection(ListenerOptions.SectionName));

        // Factory-activated (IMiddleware) like the setup gate, so it is resolved from the container.
        // Singleton: it holds the bound options and nothing per-request.
        builder.Services.AddSingleton<ListenerSegregationMiddleware>();

        // Pinned rather than left to be discovered. It guarded against a measured failure: the printer
        // listener used to be this process's only HTTPS endpoint, so the redirection middleware found
        // it and answered plain-HTTP user requests with a 307 to the *printer* port - GET / on the
        // user listener returned 307 to https://...:15443/ the first time both listeners came up.
        // That endpoint is gone and discovery would now find the right port on its own, so this is no
        // longer load-bearing; it is kept because it costs a line and because the next person to add
        // an HTTPS listener here should find the trap written down rather than measure it again.
        // Null when no user-facing HTTPS port exists, which is also why the middleware itself is only
        // registered in that case: when a proxy terminates TLS, redirecting to https is the proxy's
        // job and it knows the public port - this process does not.
        builder.Services.AddHttpsRedirection(options =>
                                                 options.HttpsPort = ListenerOptions.ReadFrom(builder.Configuration).UserHttpsPort);

        builder.WebHost.ConfigureKestrel(options =>
        {
            ListenerOptions listeners = options.ApplicationServices
                                               .GetRequiredService<IOptions<ListenerOptions>>().Value;

            listeners.Validate();

            options.ListenAnyIP(listeners.UserPort);

            if (listeners.UserHttpsPort is int userHttpsPort)
            {
                options.ListenAnyIP(userHttpsPort, listen => listen.UseHttps());
            }

            // Plain HTTP, and the same line whichever way the deployment is configured. With
            // PrusaConnect:PrinterTls on, nginx terminates in front of this port and it is never
            // published; with it off, this port is published directly and the wire is readable. The
            // difference is what sits in front, which is compose.yaml's business rather than this
            // process's - so there is one listener here and no branch.
            options.ListenAnyIP(listeners.PrinterPort);

            // Plain HTTP and never anything else - see ListenerOptions.TransferPort. The one listener
            // whose being unencrypted is the design rather than a proxy's business.
            options.ListenAnyIP(listeners.TransferPort);
        });

        return builder;
    }
}
