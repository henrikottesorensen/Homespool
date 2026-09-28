using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Localisation;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Notifications;

/// <summary>
/// A person's own notification destinations: adding a browser, listing and removing them, and sending
/// one a test.
/// </summary>
/// <remarks>
/// <b>Every method takes the owner's id and acts only on their rows.</b> A destination is named by its
/// public id in forms, and one belonging to somebody else is indistinguishable from one that does not
/// exist.
/// </remarks>
public sealed class NotificationDestinationService
{
    /// <summary>Where a notification about notifications leads: the page that manages them.</summary>
    public const string SettingsPath = "/Account/Manage/Notifications";

    /// <summary>The test notification's tag, so a second test replaces the first on screen.</summary>
    public const string TestTag = "homespool-test";

    private readonly HomespoolDbContext _db;
    private readonly WebPushEndpointPolicy _endpoints;
    private readonly IReadOnlyDictionary<Model.NotificationChannelKind, INotificationChannel> _channels;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationDestinationService> _logger;

    public NotificationDestinationService(HomespoolDbContext db,
                                          WebPushEndpointPolicy endpoints,
                                          IEnumerable<INotificationChannel> channels,
                                          IStringLocalizer<SharedResource> localiser,
                                          TimeProvider time,
                                          ILogger<NotificationDestinationService> logger)
    {
        _db = db;
        _endpoints = endpoints;
        _channels = channels.ToDictionary(channel => channel.Kind);
        _localiser = localiser;
        _time = time;
        _logger = logger;
    }

    /// <summary>The owner's destinations, oldest first.</summary>
    public async Task<IReadOnlyList<NotificationDestination>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        return await _db.NotificationDestinations
                        .AsNoTracking()
                        .Where(destination => destination.UserId == userId)
                        .OrderBy(destination => destination.CreatedAt)
                        .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores a browser's push subscription for <paramref name="userId"/>, or moves it to them if the
    /// same browser subscribed under another account before.
    /// </summary>
    /// <param name="userId">The account subscribing.</param>
    /// <param name="endpoint">The push service's address for the browser.</param>
    /// <param name="p256dh">The browser's public key, base64url.</param>
    /// <param name="auth">The browser's authentication secret, base64url.</param>
    /// <param name="name">What to call the browser in the list.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task<WebPushSubscribeResult> SubscribeWebPushAsync(long userId,
                                                                    string? endpoint,
                                                                    string? p256dh,
                                                                    string? auth,
                                                                    string name,
                                                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_endpoints.Allows(endpoint))
        {
            return WebPushSubscribeResult.EndpointNotAllowed;
        }

        if (!WebPushSubscriptionKeys.AreValid(p256dh, auth))
        {
            return WebPushSubscribeResult.KeysInvalid;
        }

        string trimmedName = name.Length > NotificationDestination.NameMaxLength ?
            name[..NotificationDestination.NameMaxLength] :
            name;

        WebPushDestination? existing = await _db.WebPushDestinations
                                                .SingleOrDefaultAsync(row => row.Endpoint == endpoint, cancellationToken);

        if (existing is null)
        {
            existing = new WebPushDestination
            {
                UserId = userId,
                Endpoint = endpoint!,
                P256dh = p256dh!,
                Auth = auth!,
                Name = trimmedName,
                CreatedAt = _time.GetUtcNow(),
            };

            _db.WebPushDestinations.Add(existing);
        }
        else
        {
            // The same browser again - after its keys were refreshed, or under whoever is signed in on
            // it now. Either way it starts over: its history belonged to the subscription it replaces.
            if (existing.UserId != userId)
            {
                _logger.LogInformation("Browser subscription {DestinationId} moved from user {PreviousUserId} to user {UserId}.",
                                       existing.Uuid, existing.UserId, userId);
            }

            existing.UserId = userId;
            existing.P256dh = p256dh!;
            existing.Auth = auth!;
            existing.Name = trimmedName;
            existing.CreatedAt = _time.GetUtcNow();
            existing.LastDeliveredAt = null;
            existing.LastFailedAt = null;
            existing.ConsecutiveFailures = 0;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} subscribed browser {DestinationId} to notifications.", userId, existing.Uuid);

        return WebPushSubscribeResult.Subscribed;
    }

    /// <summary>Removes one of the owner's destinations. False when they have none by that id.</summary>
    public async Task<bool> RemoveAsync(long userId, Guid uuid, CancellationToken cancellationToken)
    {
        int removed = await _db.NotificationDestinations
                               .Where(destination => destination.UserId == userId && destination.Uuid == uuid)
                               .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
        {
            _logger.LogInformation("User {UserId} removed notification destination {DestinationId}.", userId, uuid);
        }

        return removed > 0;
    }

    /// <summary>
    /// Sends a test notification to one of the owner's destinations, in the owner's language, and
    /// records how it went. Null when they have no destination by that id.
    /// </summary>
    public async Task<DeliveryOutcome?> SendTestAsync(long userId, Guid uuid, CancellationToken cancellationToken)
    {
        NotificationDestination? destination = await _db.NotificationDestinations
                                                        .SingleOrDefaultAsync(row => row.UserId == userId && row.Uuid == uuid,
                                                                              cancellationToken);

        if (destination is null)
        {
            return null;
        }

        string? language = await _db.Users
                                    .Where(user => user.Id == userId)
                                    .Select(user => user.Language)
                                    .SingleOrDefaultAsync(cancellationToken);

        // Composed in the account's language rather than the request's, as every notification will
        // be: most are written with no request in flight, and this one should read like them.
        NotificationMessage message = UserCultures.InCulture(language, () => new NotificationMessage(
            _localiser["Notifications_TestTitle"].Value,
            _localiser["Notifications_TestBody"].Value,
            SettingsPath,
            TestTag,
            NotificationUrgency.Normal,
            TimeSpan.FromMinutes(10)));

        DeliveryOutcome outcome = await DeliverAsync(destination, message, cancellationToken);

        return outcome;
    }

    /// <summary>
    /// Delivers through the destination's channel and records the outcome on it: the time and a reset
    /// count on success, a count on failure, and the row itself removed when the destination is gone.
    /// </summary>
    private async Task<DeliveryOutcome> DeliverAsync(NotificationDestination destination,
                                                     NotificationMessage message,
                                                     CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(destination.Kind, out INotificationChannel? channel))
        {
            throw new InvalidOperationException($"No channel delivers to a {destination.Kind} destination.");
        }

        DeliveryOutcome outcome = await channel.DeliverAsync(destination, message, cancellationToken);
        DateTimeOffset now = _time.GetUtcNow();

        switch (outcome)
        {
            case DeliveryOutcome.Delivered:
                destination.LastDeliveredAt = now;
                destination.ConsecutiveFailures = 0;
                break;

            case DeliveryOutcome.Gone:
                _db.NotificationDestinations.Remove(destination);
                _logger.LogInformation("Notification destination {DestinationId} no longer exists at its service and was removed.",
                                       destination.Uuid);
                break;

            case DeliveryOutcome.Transient:
            case DeliveryOutcome.Refused:
                destination.LastFailedAt = now;
                destination.ConsecutiveFailures++;
                break;

            default:
                throw new InvalidOperationException($"A channel answered {outcome}.");
        }

        // The delivery was not cancelled, so its record should not be either: a notification that
        // reached a phone while the request was being abandoned still reached it.
        await _db.SaveChangesAsync(CancellationToken.None);

        return outcome;
    }
}
