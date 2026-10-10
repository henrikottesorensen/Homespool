using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Notification destinations for tests that construct an account page or service by hand.
/// </summary>
/// <remarks>
/// <b>No channels.</b> These tests ask what happens to the rows, never what is delivered to them;
/// <c>WebPushRig</c> is the harness for delivery.
/// </remarks>
internal static class TestNotificationDestinations
{
    /// <summary>The destination service over <paramref name="context"/>, with nothing to deliver through.</summary>
    public static NotificationDestinationService Over(HomespoolDbContext context)
    {
        return new NotificationDestinationService(context,
                                                  new WebPushEndpointPolicy(TestOptions.Monitor(new WebPushOptions())),
                                                  [],
                                                  TestLocaliser.Shared(),
                                                  TimeProvider.System,
                                                  NullLogger<NotificationDestinationService>.Instance);
    }

    /// <summary>
    /// Subscribes a browser for <paramref name="userId"/> by writing the row directly, and returns its
    /// public id.
    /// </summary>
    public static async Task<Guid> AddBrowserAsync(HomespoolDbContext context, long userId, string name)
    {
        WebPushDestination destination = new()
        {
            UserId = userId,
            Name = name,
            Endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            P256dh = "p256dh",
            Auth = "auth",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        context.WebPushDestinations.Add(destination);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return destination.Uuid;
    }
}
