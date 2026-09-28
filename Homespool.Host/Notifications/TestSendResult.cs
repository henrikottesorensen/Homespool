namespace Homespool.Host.Notifications;

/// <summary>What pressing "Send a test" came to.</summary>
public enum TestSendResult
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>Accepted by the destination's delivery service.</summary>
    Delivered = 1,

    /// <summary>The service could not be reached, or asked to be tried later.</summary>
    Transient = 2,

    /// <summary>The destination no longer exists, and has been removed.</summary>
    Gone = 3,

    /// <summary>The service refused the request.</summary>
    Refused = 4,

    /// <summary>The account has no destination by that id.</summary>
    NotFound = 5,

    /// <summary>
    /// The destination was sent something moments ago; this was not sent. A button pressed as fast as
    /// a hand can press it is a request to the push service each time.
    /// </summary>
    TooSoon = 6,
}
