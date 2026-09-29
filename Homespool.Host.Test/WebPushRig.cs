using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The notifications registration as the application makes it, over a real SQLite file, with the
/// <see cref="FakePushService"/> standing where the network would be.
/// </summary>
/// <remarks>
/// Built from <c>AddNotifications</c> itself rather than by constructing the classes, so the named
/// client carries the same handlers it does in production - the bounded error body among them, which
/// is one of the things these tests are about.
/// </remarks>
internal sealed class WebPushRig : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private WebPushRig(ServiceProvider services, string databasePath, FakePushService pushService)
    {
        _services = services;
        DatabasePath = databasePath;
        PushService = pushService;
    }

    public string DatabasePath { get; }

    public FakePushService PushService { get; }

    public IServiceProvider Services => _services;

    /// <summary>
    /// A rig over <paramref name="databasePath"/>, migrated, whose keys are protected by
    /// <paramref name="protection"/> - pass the same one twice to be the same deployment restarted, a
    /// different one to be a deployment that lost its key ring.
    /// </summary>
    /// <param name="databasePath">The SQLite file, from <see cref="NewDatabasePath"/>.</param>
    /// <param name="protection">What protects the stored key.</param>
    /// <param name="configuration">Settings, as the configuration file would give them.</param>
    /// <param name="time">The clock; the system's when omitted.</param>
    /// <param name="realNetwork">
    /// Leaves the production handler in place, address guard and all, instead of the fake push
    /// service - for a test that must be refused before anything is reached.
    /// </param>
    /// <param name="services">Changes made after the application's registrations, which they override.</param>
    public static async Task<WebPushRig> CreateAsync(string databasePath,
                                                     IDataProtectionProvider protection,
                                                     IDictionary<string, string?>? configuration = null,
                                                     TimeProvider? time = null,
                                                     bool realNetwork = false,
                                                     Action<IServiceCollection>? services = null)
    {
        FakePushService pushService = new();

        ServiceCollection registrations = new();
        registrations.AddLogging();
        registrations.AddLocalization();
        registrations.AddSingleton(time ?? TimeProvider.System);

        // The notification watcher asks it which printers are connected.
        registrations.AddSingleton<Printing.PrinterConnectionRegistry>();
        registrations.AddSingleton(protection);
        registrations.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        registrations.AddNotifications(new ConfigurationBuilder()
                                       .AddInMemoryCollection(configuration ?? new Dictionary<string, string?>())
                                       .Build());

        // Replaces the primary handler, and with it the address guard, which has tests of its own.
        // Kept for the rig's life: the factory would otherwise dispose the one shared instance when its
        // handler lifetime ran out.
        if (!realNetwork)
        {
            registrations.AddHttpClient(WebPushChannel.HttpClientName)
                         .ConfigurePrimaryHttpMessageHandler(() => pushService)
                         .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }

        services?.Invoke(registrations);

        ServiceProvider provider = registrations.BuildServiceProvider();

        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                       .Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        return new WebPushRig(provider, databasePath, pushService);
    }

    /// <summary>A new temporary database path, for <see cref="Delete"/> to remove afterwards.</summary>
    public static string NewDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hs-webpush-{Guid.NewGuid():N}.db");
    }

    /// <summary>Removes a database and its WAL companions.</summary>
    public static void Delete(string databasePath)
    {
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Adds an account, optionally with a stored language.</summary>
    public async Task<HSUser> AddUserAsync(string email, string? language = null)
    {
        using IServiceScope scope = _services.CreateScope();
        HomespoolDbContext db = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HSUser user = new(email)
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            Language = language,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    /// <summary>Stores <paramref name="browser"/> as <paramref name="userId"/>'s, bypassing the checks.</summary>
    public async Task<WebPushDestination> AddBrowserAsync(long userId, FakePushBrowser browser)
    {
        using IServiceScope scope = _services.CreateScope();
        HomespoolDbContext db = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        WebPushDestination destination = new()
        {
            UserId = userId,
            Endpoint = browser.Endpoint,
            P256dh = browser.P256dh,
            Auth = browser.Auth,
            Name = "Test browser",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.WebPushDestinations.Add(destination);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return destination;
    }

    /// <summary>Runs <paramref name="action"/> against a scoped service and a context of its own.</summary>
    /// <typeparam name="T">What the action answers.</typeparam>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = _services.CreateScope();

        return await action(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        PushService.Dispose();
    }
}
