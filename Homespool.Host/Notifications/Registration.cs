using System;
using System.Net.Http;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Notifications.WebPush;

namespace Homespool.Host.Notifications;

/// <summary>
/// Wires up notifications, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
public static class Registration
{
    /// <summary>
    /// Adds the destinations service, the Web Push channel and the guarded client it sends through.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WebPushOptions>()
                .Bind(configuration.GetSection(WebPushOptions.SectionName))
                .Validate(options => WebPushOptions.IsValidContact(options.Contact),
                          $"{WebPushOptions.SectionName}:{nameof(WebPushOptions.Contact)} must be a mailto: or an https: address.")
                .ValidateOnStart();

        // The one client in the application that connects to an address somebody else chose, so
        // everything about where it may go is decided here rather than at a call site.
        //
        // No redirects: a push service answers where it was asked, and a redirect is a second
        // destination nothing checked by name. No proxy: the guard decides by the address connected
        // to, and through a proxy that would be the proxy's - a deployment that reaches the internet
        // only through one cannot send push notifications, which is the price of the check meaning
        // something.
        services.AddHttpClient(WebPushChannel.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    ConnectCallback = PushAddressGuard.ConnectAsync,
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                .AddHttpMessageHandler(() => new BoundedErrorBodyHandler());

        // Singletons: the policy reads options and holds nothing; the key store is the key, held for the
        // life of the process once read; the channel holds only those two and the client factory.
        services.AddSingleton<WebPushEndpointPolicy>();
        services.AddSingleton<VapidKeyStore>();
        services.AddSingleton<WebPushChannel>();
        services.AddSingleton<INotificationChannel>(provider => provider.GetRequiredService<WebPushChannel>());

        // Scoped: holds a DbContext.
        services.AddScoped<NotificationDestinationService>();

        // What notices things to say. The attention watch is the telemetry writer's observer - the one
        // place a printer's state before and after a message both exist - and the watcher reads what
        // the queue has committed. Both publish to one queue, which never makes them wait.
        services.AddSingleton<NotificationQueue>();
        services.AddSingleton<AttentionWatch>();
        services.AddSingleton<Telemetry.ILiveStateObserver>(provider => provider.GetRequiredService<AttentionWatch>());
        services.AddSingleton<FilamentChangeWatch>();
        services.AddSingleton<Telemetry.ILiveStateObserver>(provider => provider.GetRequiredService<FilamentChangeWatch>());

        // Resolvable as itself as well as a hosted service, following QueueAdvancer: a test drives one
        // look rather than waiting out the interval.
        services.AddSingleton<NotificationWatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationWatcher>());

        // Singleton because it is the memory of what was sent, per printer, across every happening.
        services.AddSingleton<NotificationThrottle>();

        // Scoped, like the destinations it delivers through; the dispatcher makes one per happening.
        services.AddScoped<NotificationRouter>();
        services.AddHostedService<NotificationDispatcher>();

        return services;
    }
}
