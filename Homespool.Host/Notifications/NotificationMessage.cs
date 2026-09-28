using System;

namespace Homespool.Host.Notifications;

/// <summary>
/// What a notification says, composed once per recipient in their language and handed to whichever
/// channel delivers it.
/// </summary>
/// <remarks>
/// <b>Nothing in it belongs to a channel.</b> A browser shows <see cref="Title"/> and
/// <see cref="Body"/> and opens <see cref="Url"/> when tapped; a webhook will post the same fields as
/// JSON. A channel that needs something this does not say is a sign the message is missing a field,
/// not that the channel should work it out itself.
/// </remarks>
/// <param name="Title">The headline, already localised.</param>
/// <param name="Body">The sentence under it, already localised.</param>
/// <param name="Url">
/// Where tapping it goes, as a path on this deployment (<c>/Printers/Detail/…</c>). Relative, because the
/// application is never told the name people reach it by; the browser resolves it against the origin
/// that subscribed.
/// </param>
/// <param name="Tag">
/// What a later notification replaces: two with the same tag show as one, the newer. At most
/// <see cref="MaxTagLength"/> URL-safe characters, which is what a push service accepts as a topic.
/// </param>
/// <param name="Urgency">How hard the delivery service should try to wake a device for it.</param>
/// <param name="TimeToLive">How long it is worth delivering at all; after that it is dropped undelivered.</param>
public sealed record NotificationMessage(string Title,
                                         string Body,
                                         string Url,
                                         string Tag,
                                         NotificationUrgency Urgency,
                                         TimeSpan TimeToLive)
{
    /// <summary>
    /// The longest <see cref="Tag"/>: RFC 8030 bounds a push message's topic at 32 characters of the
    /// URL-safe base64 alphabet.
    /// </summary>
    public const int MaxTagLength = 32;
}
