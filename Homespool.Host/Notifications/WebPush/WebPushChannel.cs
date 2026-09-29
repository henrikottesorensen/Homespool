using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Lib.Net.Http.WebPush;

using Microsoft.Extensions.Logging;

using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Delivers to a browser through its push service: encrypts the message to the subscription's keys
/// (RFC 8291), signs the request with the deployment's key (RFC 8292) and posts it to the endpoint
/// (RFC 8030).
/// </summary>
/// <remarks>
/// <para>
/// <b>The protocol is the library's; what surrounds it is ours.</b> <c>Lib.Net.Http.WebPush</c> does
/// the encryption and the signature. Three of its defaults are unsafe for us and are overridden or
/// covered here: it retries a 429 as often as told to with no cap, so that is turned off and a 429 is
/// <see cref="DeliveryOutcome.Transient"/>; it sends a payload of any size, so the size is checked
/// first; and it reads an error body whole and without a cancellation token, which
/// <see cref="BoundedErrorBodyHandler"/> takes care of on the named client.
/// </para>
/// <para>
/// <b>An endpoint is never logged.</b> It is a capability URL, and the library's exception carries it -
/// so failures are logged by the destination's public id, the status code and what the push service
/// said, and the exception object is not handed to the logger. What it said is logged because a status
/// alone does not say which header was wrong: Apple's push service answers with a 400 or a 403 and
/// puts the reason in the body.
/// </para>
/// <para>
/// <b>No <c>Topic</c> header</b>, although RFC 8030 offers one for replacing a message still queued
/// for an offline device. Apple's push service has been refusing pushes that carry one with
/// <c>400 BadWebPushTopic</c>, and what it would buy is already done on screen: the message's tag goes
/// in the payload, and the browser replaces a notification with the next one of the same tag.
/// </para>
/// </remarks>
public sealed class WebPushChannel : INotificationChannel
{
    /// <summary>The named <see cref="HttpClient"/> every push request goes out on.</summary>
    public const string HttpClientName = "WebPush";

    /// <summary>
    /// The longest payload that fits: a push service must accept a 4096-byte body, and the encryption
    /// adds an 86-byte header, a 16-byte tag and a one-byte delimiter to what it is given.
    /// </summary>
    public const int MaxPayloadBytes = 4096 - 86 - 16 - 1;

    /// <summary>
    /// How much of a push service's answer a log line keeps: a reason is a word or a sentence, and the
    /// body is the other side's to make as long as it likes.
    /// </summary>
    private const int MaxLoggedAnswerLength = 200;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly VapidKeyStore _keys;
    private readonly WebPushEndpointPolicy _policy;
    private readonly ILogger<WebPushChannel> _logger;

    public WebPushChannel(IHttpClientFactory clients,
                          VapidKeyStore keys,
                          WebPushEndpointPolicy policy,
                          ILogger<WebPushChannel> logger)
    {
        _clients = clients;
        _keys = keys;
        _policy = policy;
        _logger = logger;
    }

    public NotificationChannelKind Kind => NotificationChannelKind.WebPush;

    public async Task<DeliveryOutcome> DeliverAsync(NotificationDestination destination,
                                                    NotificationMessage message,
                                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(message);

        if (destination is not WebPushDestination browser)
        {
            throw new ArgumentException($"A {destination.Kind} destination was handed to the Web Push channel.", nameof(destination));
        }

        // Asked again at every delivery, not only when the row was stored: the allowlist is
        // configuration, and a host an operator has since removed must stop being sent to.
        if (!_policy.Allows(browser.Endpoint))
        {
            _logger.LogWarning("Not sending to browser subscription {DestinationId}: its push endpoint is not on the allowed list.",
                               browser.Uuid);

            return DeliveryOutcome.Refused;
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new WebPushPayload(message.Title, message.Body, message.Url, message.Tag), PayloadJson);

        if (payload.Length > MaxPayloadBytes)
        {
            _logger.LogWarning("Not sending to browser subscription {DestinationId}: the message is {PayloadBytes} bytes, more than a push service accepts.",
                               browser.Uuid, payload.Length);

            return DeliveryOutcome.Refused;
        }

        VapidCredentials credentials = await _keys.GetAsync(cancellationToken).ConfigureAwait(false);

        PushServiceClient client = new(_clients.CreateClient(HttpClientName))
        {
            AutoRetryAfter = false,
            DefaultAuthentication = credentials.Authentication,
        };

        PushSubscription subscription = new() { Endpoint = browser.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, browser.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, browser.Auth);

        using ByteArrayContent content = new(payload);

        PushMessage push = new(content)
        {
            Urgency = ToPushUrgency(message.Urgency),
            TimeToLive = (int)Math.Clamp(message.TimeToLive.TotalSeconds, 0, int.MaxValue),
        };

        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, push, cancellationToken).ConfigureAwait(false);

            return DeliveryOutcome.Delivered;
        }
        catch (PushServiceClientException refusal)
        {
            DeliveryOutcome outcome = ForStatus(refusal.StatusCode);

            _logger.LogWarning("The push service answered {StatusCode} for browser subscription {DestinationId}, which counts as {Outcome}: {Answer}",
                               (int)refusal.StatusCode, browser.Uuid, outcome, LogText.Clean(refusal.Body, MaxLoggedAnswerLength));

            return outcome;
        }
        catch (HttpRequestException failure) when (failure.InnerException is PushAddressRefusedException)
        {
            _logger.LogWarning("Not sending to browser subscription {DestinationId}: its push service resolved to no public address.",
                               browser.Uuid);

            return DeliveryOutcome.Refused;
        }
        catch (HttpRequestException failure)
        {
            _logger.LogWarning("Could not reach the push service for browser subscription {DestinationId} ({Error}).",
                               browser.Uuid, failure.HttpRequestError);

            return DeliveryOutcome.Transient;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout, which arrives as a cancellation nobody asked for.
            _logger.LogWarning("The push service for browser subscription {DestinationId} did not answer in time.",
                               browser.Uuid);

            return DeliveryOutcome.Transient;
        }
        catch (CryptographicException)
        {
            // Keys that passed the check at the door and still cannot be encrypted to.
            _logger.LogWarning("Browser subscription {DestinationId} has keys that cannot be encrypted to.",
                               browser.Uuid);

            return DeliveryOutcome.Refused;
        }
    }

    /// <summary>
    /// What a push service's refusal means for the destination.
    /// </summary>
    /// <remarks>
    /// <b>A 403 is a refusal, not a disappearance</b>, although a push service answers it when a
    /// subscription was made with another key. It is also what a malformed signature earns, and
    /// treating that as "gone" would delete every subscription over one mistake of ours.
    /// </remarks>
    public static DeliveryOutcome ForStatus(HttpStatusCode status)
    {
        int code = (int)status;

        return code switch
        {
            404 or 410 => DeliveryOutcome.Gone,
            408 or 429 or >= 500 => DeliveryOutcome.Transient,
            _ => DeliveryOutcome.Refused,
        };
    }

    private static PushMessageUrgency ToPushUrgency(NotificationUrgency urgency)
    {
        return urgency switch
        {
            NotificationUrgency.VeryLow => PushMessageUrgency.VeryLow,
            NotificationUrgency.Low => PushMessageUrgency.Low,
            NotificationUrgency.High => PushMessageUrgency.High,
            _ => PushMessageUrgency.Normal,
        };
    }

    /// <summary>What the service worker receives, as JSON. Lower-case names, as a script reads them.</summary>
    private sealed record WebPushPayload(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("tag")] string Tag);
}
