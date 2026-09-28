namespace Homespool.Host.Notifications;

/// <summary>
/// What became of one attempt to deliver one notification to one destination.
/// </summary>
/// <remarks>
/// Four answers, because each asks something different of whoever reacts to it: nothing, try again
/// later, forget the destination, or stop and look at what was sent. A channel maps its own protocol's
/// answers onto these and nothing above it needs to know a status code.
/// </remarks>
public enum DeliveryOutcome
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>Accepted by whatever delivers it onward.</summary>
    Delivered = 1,

    /// <summary>
    /// Not delivered this time, for a reason that may pass: the network, the service being busy or
    /// asking us to slow down.
    /// </summary>
    Transient = 2,

    /// <summary>
    /// The destination no longer exists - the browser unsubscribed, or the subscription expired - and
    /// will never accept anything again. It is deleted.
    /// </summary>
    Gone = 3,

    /// <summary>
    /// Refused for something about the request itself, so sending it again unchanged would be refused
    /// again: an address that is not allowed, keys that do not work, a message too large.
    /// </summary>
    Refused = 4,
}
