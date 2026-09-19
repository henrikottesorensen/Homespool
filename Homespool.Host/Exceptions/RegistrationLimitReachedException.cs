using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A printer asked for a registration code while its fingerprint already holds as many pending
/// registrations as it may, and none of them can be dropped to make room.
/// </summary>
/// <remarks>
/// Only claimed registrations cannot be dropped, so this takes several claims whose printers never
/// came back for a token. It clears by itself as those expire.
/// </remarks>
public class RegistrationLimitReachedException : Exception
{
    public RegistrationLimitReachedException()
        : base("This printer already has as many claimed registrations pending as it may.")
    {
    }

    public RegistrationLimitReachedException(string message)
        : base(message)
    {
    }

    public RegistrationLimitReachedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
