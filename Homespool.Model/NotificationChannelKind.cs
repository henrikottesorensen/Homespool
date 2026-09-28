namespace Homespool.Model;

/// <summary>
/// How a notification reaches somebody: the kind of a <see cref="Entities.NotificationDestination"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One member per delivery mechanism, and each has its own row type.</b> This is the table's
/// discriminator, so a new kind of destination - a webhook, an ntfy topic - is a new member here, a new
/// subclass of <see cref="Entities.NotificationDestination"/> carrying what that kind needs, and a
/// channel in the host that knows how to deliver to it. Nothing that decides <i>whether</i> to notify
/// changes.
/// </para>
/// <para>
/// Stored as text, like every enum column here: a row is read by somebody working out why a
/// notification did not arrive, and the name says more than a number.
/// </para>
/// </remarks>
public enum NotificationChannelKind
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>
    /// The browser's own push service (RFC 8030), reached through the endpoint a browser handed us
    /// when it subscribed.
    /// </summary>
    WebPush = 1,
}
