using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Wires up camera reading, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
/// <remarks>
/// The handler configuration is the reason this is worth its own file: it is the address policy,
/// and it reads as networking plumbing in the middle of a startup file that is otherwise a list of
/// features. <c>Authorisation/Builder.cs</c> is the same idea for authorisation policies.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Adds camera options, the guarded HTTP client, the fetcher and the frame cache.
    /// </summary>
    public static IServiceCollection AddCameras(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CameraOptions>(configuration.GetSection(CameraOptions.SectionName));

        // A named client, not the default one. This is the application's first outbound HTTP, and
        // its handler carries an address policy that has no business applying to the second.
        services.AddHttpClient(CameraSnapshotFetcher.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(BuildHandler);

        services.AddSingleton<ICameraSnapshotFetcher, CameraSnapshotFetcher>();

        // Singleton because it is the cache: the frames it holds are the whole point, and a scoped
        // one would hold nothing between two requests of the same page.
        services.AddSingleton<CameraFrameCache>();

        return services;
    }

    private static HttpMessageHandler BuildHandler(IServiceProvider serviceProvider)
    {
        IOptions<CameraOptions> options = serviceProvider.GetRequiredService<IOptions<CameraOptions>>();

        SocketsHttpHandler handler = new()
        {
            // A camera address is operator-supplied and fetched by the server, so it reaches
            // whatever the server can. A redirect would otherwise walk past the address check
            // below to somewhere never approved - the check and the connection are two different
            // moments, and a 302 gets a second connection.
            AllowAutoRedirect = false,
        };

        if (options.Value.RefuseLoopbackAndLinkLocal)
        {
            handler.ConnectCallback = ConnectOnlyToReachableAddressesAsync;
        }

        return handler;
    }

    /// <summary>
    /// Resolves the host and refuses loopback and link-local before connecting.
    /// </summary>
    /// <remarks>
    /// Here rather than against the typed address because this is the only place the resolved
    /// address is known: a check on a string cannot see where a hostname points. It does not close
    /// DNS rebinding, which would need the resolved address pinned for the life of the connection -
    /// recorded as a known limit on <see cref="CameraOptions.RefuseLoopbackAndLinkLocal"/> rather
    /// than left to be discovered.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "Ownership passes to NetworkStream, constructed with ownsSocket: true, which disposes the socket when the connection ends. Disposing it here would close the connection before a byte crossed it.")]
    private static async ValueTask<Stream> ConnectOnlyToReachableAddressesAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);

        foreach (IPAddress address in addresses)
        {
            if (!CameraAddressPolicy.IsReachableAddress(address))
            {
                throw new HttpRequestException(
                    $"Refusing to fetch a camera from {address}: loopback and link-local addresses "
                    + "are not reachable camera locations.");
            }
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                        .ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
