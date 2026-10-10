namespace Homespool.Host.Notifications;

/// <summary>Whether a browser's push subscription was stored, and if not, why.</summary>
public enum WebPushSubscribeResult
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>Stored, or its keys refreshed.</summary>
    Subscribed = 1,

    /// <summary>The endpoint is not one of the push services Homespool sends to.</summary>
    EndpointNotAllowed = 2,

    /// <summary>The keys are not a P-256 public key on the curve and a 16-byte secret.</summary>
    KeysInvalid = 3,

    /// <summary>The account already has as many destinations as one may.</summary>
    TooMany = 4,

    /// <summary>
    /// Another account has this endpoint. A browser changing accounts subscribes afresh, so this is a
    /// browser that did not, or somebody posting an endpoint that is not theirs.
    /// </summary>
    EndpointTaken = 5,
}
