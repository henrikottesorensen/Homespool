using System;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        // itself, the only address these dial is one the operator configured, and the check that
        // matters moved to CameraSourcePolicy - which runs before a source is handed over.
        services.AddHttpClient(CameraSnapshotFetcher.HttpClientName);
        services.AddHttpClient(Go2RtcClient.HttpClientName);

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
}
