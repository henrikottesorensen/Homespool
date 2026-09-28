using System;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// A push endpoint's host resolved to no public address, so the connection was never attempted.
/// </summary>
/// <remarks>
/// Its own type so that the channel can tell it from a network failure: this one will not pass, and
/// is answered as a refusal rather than retried.
/// </remarks>
public sealed class PushAddressRefusedException : Exception
{
    public PushAddressRefusedException()
    {
    }

    public PushAddressRefusedException(string host)
        : base($"The push endpoint's host {host} has no public address, so no connection was made.")
    {
    }

    public PushAddressRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
