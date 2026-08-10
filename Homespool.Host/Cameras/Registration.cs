using System;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Wires up cameras, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
/// <remarks>
/// <c>Authorisation/Builder.cs</c> is the same idea for authorisation policies: the configuration
/// for a thing lives beside the thing.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Adds camera options, the stream-server client, the frame cache and the startup reconciler.
    /// </summary>
    public static IServiceCollection AddCameras(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CameraOptions>(configuration.GetSection(CameraOptions.SectionName));

        // Both clients reach only the sidecar, on the Compose network. There is deliberately no
        // address policy on the handler any more: since Homespool stopped fetching camera sources
        // itself, the only address these connect to is one the operator configured, and the check that
        // matters moved to CameraSourcePolicy - which runs before a source is handed over.
        //
        // The credential is applied here rather than at each call site, so no request can be written
        // that forgets it - the same reasoning that keeps the permission check inside the services.
        services.AddHttpClient(CameraSnapshotFetcher.HttpClientName).ConfigureHttpClient(ApplyCredential);
        services.AddHttpClient(Go2RtcClient.HttpClientName).ConfigureHttpClient(ApplyCredential);

        services.AddSingleton<ICameraSnapshotFetcher, CameraSnapshotFetcher>();
        services.AddSingleton<Go2RtcClient>();

        // Singleton because it is the cache: the frames it holds are the whole point, and a scoped
        // one would hold nothing between two requests of the same page.
        services.AddSingleton<CameraFrameCache>();

        // Both dependencies are singletons and it holds no state of its own.
        services.AddSingleton<CameraSourcePolicy>();

        // Reads a bind-mounted directory; nothing per-request about it.
        services.AddSingleton<LocalCameraDevices>();

        // Scoped: both hold a DbContext, and the access gate memoises within a request.
        services.AddScoped<Authorisation.CameraAccessService>();
        services.AddScoped<CameraService>();

        // Runs once at startup, after MigrateHomespoolData has made the tables exist.
        services.AddHostedService<CameraStreamReconciler>();

        return services;
    }

    /// <summary>
    /// Puts the stream server's credential on a client, if one is configured.
    /// </summary>
    /// <remarks>
    /// Silently does nothing when either half is empty, which is the unauthenticated arrangement a
    /// deployment that never published the sidecar's port already had. Sending half a credential
    /// would fail every request for a reason nobody could see.
    /// </remarks>
    private static void ApplyCredential(IServiceProvider serviceProvider, System.Net.Http.HttpClient client)
    {
        CameraOptions options = serviceProvider.GetRequiredService<IOptions<CameraOptions>>().Value;

        if (string.IsNullOrEmpty(options.ApiUsername) || string.IsNullOrEmpty(options.ApiPassword))
        {
            return;
        }

        string pair = $"{options.ApiUsername}:{options.ApiPassword}";

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
    }
}
