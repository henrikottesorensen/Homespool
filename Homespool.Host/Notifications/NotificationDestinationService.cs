using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Accounts;
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

    /// <summary>
    /// How many destinations one account may have. Far more than anybody's browsers and phones, and a
    /// bound on how many requests one event can make the server send on one person's behalf.
    /// </summary>
    public const int MaxPerAccount = 16;

    /// <summary>
    /// How many refusals in a row remove a destination. More than one, so a single mistake of ours - a
    /// malformed request refused for every destination at once - does not delete them all.
    /// </summary>
    public const int RemoveAfterRefusals = 5;

    /// <summary>How soon after anything was last sent to a destination a test may be sent to it.</summary>
    public static readonly TimeSpan TestCooldown = TimeSpan.FromSeconds(10);

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
    /// Stores a browser's push subscription for <paramref name="userId"/>, or refreshes its keys if they
    /// have it already. One another account has is refused.
    /// </summary>
    /// <remarks>
    /// <b>A subscription never changes accounts here.</b> An endpoint can be learned without the browser
    /// it reaches, so posting one proves nothing, and anything sent on the poster's behalf lands on the
    /// owner's push service - where a message the browser cannot decrypt still spends the subscription's
    /// quota, and Firefox unsubscribes it after sixteen between visits to the site. A browser changing
    /// hands subscribes afresh instead, which gives it a new endpoint and leaves the previous account's
    /// gone at its service.
    /// </remarks>
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

        if (existing is not null && existing.UserId != userId)
        {
            _logger.LogWarning("User {UserId} posted the endpoint of browser {DestinationId}, which belongs to user {OwnerId}; refused.",
                               userId, existing.Uuid, existing.UserId);

            return WebPushSubscribeResult.EndpointTaken;
        }

        if (existing is null &&
            await _db.NotificationDestinations.CountAsync(row => row.UserId == userId, cancellationToken) >= MaxPerAccount)
        {
            return WebPushSubscribeResult.TooMany;
        }

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
            // The same browser again, after its keys were refreshed. It starts over: its history
            // belonged to the subscription it replaces.
            existing.P256dh = p256dh!;
            existing.Auth = auth!;
            existing.Name = trimmedName;
            existing.CreatedAt = _time.GetUtcNow();
            existing.LastDeliveredAt = null;
            existing.LastFailedAt = null;
            existing.ConsecutiveFailures = 0;
            existing.ConsecutiveRefusals = 0;
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
    /// Removes every destination <paramref name="userId"/> has, and says how many there were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For the moments an account is taken back</b>: a password reset by emailed link, a redeemed
    /// recovery invite, and an administrator's closure. A browser subscription outlives the session
    /// that made it - signing out leaves it in place - so one added by a stolen session would otherwise
    /// go on hearing about the account's printers, and about the owner's own clean-up, after the
    /// account was recovered. The owner's browsers go too, and their pages offer to subscribe again.
    /// </para>
    /// <para>
    /// <b>Bulk and untracked, like <see cref="ApiTokenService.RevokeAllForUserAsync"/></b>,
    /// so it joins whatever transaction its caller holds and lands or rolls back with the rest of the
    /// recovery. Logging is the caller's, beside the other counts it reports.
    /// </para>
    /// </remarks>
    public Task<int> RemoveAllAsync(long userId, CancellationToken cancellationToken)
    {
        return _db.NotificationDestinations
                  .Where(destination => destination.UserId == userId)
                  .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Sends a test notification to one of the owner's destinations, in the owner's language, and
    /// records how it went.
    /// </summary>
    /// <remarks>
    /// <b>Not more often than <see cref="TestCooldown"/> per destination</b>, measured from whatever
    /// was last sent to it. Each press is a request from this server to a push service, and a button
    /// has no other limit on how fast it can be pressed.
    /// </remarks>
    public async Task<TestSendResult> SendTestAsync(long userId, Guid uuid, CancellationToken cancellationToken)
    {
        NotificationDestination? destination = await _db.NotificationDestinations
                                                        .SingleOrDefaultAsync(row => row.UserId == userId && row.Uuid == uuid,
                                                                              cancellationToken);

        if (destination is null)
        {
            return TestSendResult.NotFound;
        }

        DateTimeOffset now = _time.GetUtcNow();

        if ((destination.LastDeliveredAt is DateTimeOffset delivered && now - delivered < TestCooldown) ||
            (destination.LastFailedAt is DateTimeOffset failed && now - failed < TestCooldown))
        {
            return TestSendResult.TooSoon;
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

        return outcome switch
        {
            DeliveryOutcome.Delivered => TestSendResult.Delivered,
            DeliveryOutcome.Transient => TestSendResult.Transient,
            DeliveryOutcome.Gone => TestSendResult.Gone,
            DeliveryOutcome.Refused => TestSendResult.Refused,
            _ => throw new InvalidOperationException($"A channel answered {outcome}."),
        };
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to every destination <paramref name="userId"/> has, and
    /// answers how many accepted it.
    /// </summary>
    /// <remarks>
    /// Each destination's outcome is recorded on it as a test send's is, so a browser that stopped
    /// hearing shows as failing on the settings page, and one its service says is gone disappears.
    /// </remarks>
    public async Task<int> DeliverToAllAsync(long userId, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        List<NotificationDestination> destinations = await _db.NotificationDestinations
                                                              .Where(destination => destination.UserId == userId)
                                                              .ToListAsync(cancellationToken);
        int delivered = 0;

        foreach (NotificationDestination destination in destinations)
        {
            if (await DeliverAsync(destination, message, cancellationToken) == DeliveryOutcome.Delivered)
            {
                delivered++;
            }
        }

        return delivered;
    }

    /// <summary>
    /// Records how a delivery made without this service went, on the destination it went to - by the
    /// same rules as one made through it.
    /// </summary>
    /// <remarks>
    /// For a sender that holds destinations it read earlier, because it must still be able to send
    /// when the database cannot be read. A destination deleted since is left deleted.
    /// </remarks>
    public async Task RecordAsync(Guid uuid, DeliveryOutcome outcome, CancellationToken cancellationToken)
    {
        NotificationDestination? destination = await _db.NotificationDestinations
                                                        .SingleOrDefaultAsync(row => row.Uuid == uuid, cancellationToken);

        if (destination is null)
        {
            return;
        }

        Record(destination, outcome);

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The kinds <paramref name="userId"/> can turn off: the administrators' own as well, for an open
    /// administrator.
    /// </summary>
    public async Task<IReadOnlyList<Model.NotificationKind>> ChoosableAsync(long userId, CancellationToken cancellationToken)
    {
        bool administrator = await Administrators.Open(_db).ContainsAsync(userId, cancellationToken);

        return NotificationMutes.ChoosableBy(administrator);
    }

    /// <summary>The kinds <paramref name="userId"/> has turned off.</summary>
    public async Task<IReadOnlySet<Model.NotificationKind>> MutedAsync(long userId, CancellationToken cancellationToken)
    {
        string? stored = await _db.Users
                                  .Where(user => user.Id == userId)
                                  .Select(user => user.MutedNotifications)
                                  .SingleOrDefaultAsync(cancellationToken);

        return NotificationMutes.Parse(stored);
    }

    /// <summary>The printers <paramref name="userId"/> has muted.</summary>
    public async Task<IReadOnlySet<Guid>> MutedPrintersAsync(long userId, CancellationToken cancellationToken)
    {
        string? stored = await _db.Users
                                  .Where(user => user.Id == userId)
                                  .Select(user => user.MutedPrinters)
                                  .SingleOrDefaultAsync(cancellationToken);

        return NotificationMutes.ParsePrinters(stored);
    }

    /// <summary>Mutes exactly <paramref name="muted"/> for <paramref name="userId"/>, and no other printer.</summary>
    public async Task SetMutedPrintersAsync(long userId, IEnumerable<Guid> muted, CancellationToken cancellationToken)
    {
        string? stored = NotificationMutes.FormatPrinters(muted);

        await _db.Users
                 .Where(user => user.Id == userId)
                 .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.MutedPrinters, stored),
                                     cancellationToken);
    }

    /// <summary>Turns off exactly <paramref name="muted"/> for <paramref name="userId"/>, and nothing else.</summary>
    public async Task SetMutedAsync(long userId, IEnumerable<Model.NotificationKind> muted, CancellationToken cancellationToken)
    {
        string? stored = NotificationMutes.Format(muted);

        await _db.Users
                 .Where(user => user.Id == userId)
                 .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.MutedNotifications, stored),
                                     cancellationToken);
    }

    /// <summary>Delivers through the destination's channel and records the outcome on it.</summary>
    private async Task<DeliveryOutcome> DeliverAsync(NotificationDestination destination,
                                                     NotificationMessage message,
                                                     CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(destination.Kind, out INotificationChannel? channel))
        {
            throw new InvalidOperationException($"No channel delivers to a {destination.Kind} destination.");
        }

        DeliveryOutcome outcome = await channel.DeliverAsync(destination, message, cancellationToken);

        Record(destination, outcome);

        // The delivery was not cancelled, so its record should not be either: a notification that
        // reached a phone while the request was being abandoned still reached it.
        await _db.SaveChangesAsync(CancellationToken.None);

        return outcome;
    }

    /// <summary>
    /// Applies <paramref name="outcome"/> to the tracked <paramref name="destination"/>: the time and
    /// reset counts on success, counts on failure, and the row itself removed when the destination is
    /// gone - or has been refused <see cref="RemoveAfterRefusals"/> times running. Saves nothing.
    /// </summary>
    private void Record(NotificationDestination destination, DeliveryOutcome outcome)
    {
        DateTimeOffset now = _time.GetUtcNow();

        switch (outcome)
        {
            case DeliveryOutcome.Delivered:
                destination.LastDeliveredAt = now;
                destination.ConsecutiveFailures = 0;
                destination.ConsecutiveRefusals = 0;
                break;

            case DeliveryOutcome.Gone:
                _db.NotificationDestinations.Remove(destination);
                _logger.LogInformation("Notification destination {DestinationId} no longer exists at its service and was removed.",
                                       destination.Uuid);
                break;

            case DeliveryOutcome.Transient:
                destination.LastFailedAt = now;
                destination.ConsecutiveFailures++;
                destination.ConsecutiveRefusals = 0;
                break;

            case DeliveryOutcome.Refused:
                destination.LastFailedAt = now;
                destination.ConsecutiveFailures++;
                destination.ConsecutiveRefusals++;

                if (destination.ConsecutiveRefusals >= RemoveAfterRefusals)
                {
                    _db.NotificationDestinations.Remove(destination);
                    _logger.LogWarning("Notification destination {DestinationId} was refused {Refusals} times running and was removed.",
                                       destination.Uuid, destination.ConsecutiveRefusals);
                }

                break;

            default:
                throw new InvalidOperationException($"A channel answered {outcome}.");
        }
    }
}
