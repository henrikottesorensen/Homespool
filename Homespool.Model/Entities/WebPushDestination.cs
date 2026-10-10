namespace Homespool.Model.Entities;

/// <summary>
/// One browser's push subscription: where its push service accepts messages for it, and the keys a
/// message is encrypted to.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Endpoint"/> is a capability URL.</b> Anyone holding it, together with the keys, can
/// put a notification on that browser's screen, and the push service asks for nothing more than a
/// signature it cannot tie to us. So it is never logged and never rendered into a page; the row is
/// named by <see cref="NotificationDestination.Uuid"/> everywhere outside this table.
/// </para>
/// <para>
/// <b>Unique, because a subscription belongs to one browser profile and so to one account.</b> It never
/// moves to another: the endpoint can be learned without the browser, so posting it proves nothing. A
/// browser somebody else signs in on subscribes afresh for them, under a new endpoint, and the previous
/// account's row goes when its push service answers that the old one is gone.
/// </para>
/// <para>
/// <b>The address is the browser's choice, not ours</b>, which makes it the one address a person
/// supplies that Homespool itself connects to. It is checked against the known push services when it
/// is stored and again before every delivery.
/// </para>
/// </remarks>
public class WebPushDestination : NotificationDestination
{
    /// <summary>
    /// The longest <see cref="Endpoint"/> accepted. The push services' own run to a few hundred
    /// characters; this is headroom, and a bound so a pasted mistake fails at the edge.
    /// </summary>
    public const int EndpointMaxLength = 1024;

    /// <summary>
    /// The longest <see cref="P256dh"/>: a 65-byte uncompressed P-256 point is 87 base64url characters.
    /// </summary>
    public const int P256dhMaxLength = 128;

    /// <summary>The longest <see cref="Auth"/>: a 16-byte secret is 22 base64url characters.</summary>
    public const int AuthMaxLength = 64;

    public WebPushDestination()
        : base(NotificationChannelKind.WebPush)
    {
    }

    /// <summary>The push service's address for this browser.</summary>
    public required string Endpoint { get; set; }

    /// <summary>
    /// The browser's public key, base64url, that each message is encrypted to (RFC 8291's
    /// <c>ua_public</c>).
    /// </summary>
    public required string P256dh { get; set; }

    /// <summary>
    /// The browser's authentication secret, base64url (RFC 8291's <c>auth_secret</c>). Not a credential
    /// for anything here - it is mixed into the encryption so only this subscription can read a message.
    /// </summary>
    public required string Auth { get; set; }
}
