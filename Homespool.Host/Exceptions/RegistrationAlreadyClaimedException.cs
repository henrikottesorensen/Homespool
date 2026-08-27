using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The registration named by a claim's code already has a <c>Printer</c> attached.
/// </summary>
/// <remarks>
/// A concurrent claim of the same code: the first claim wins and every later one is rejected,
/// rather than silently overwriting the printer the first claim created.
/// </remarks>
public class RegistrationAlreadyClaimedException : Exception
{
    public RegistrationAlreadyClaimedException()
        : base("The printer identified by this registration code has already been claimed.")
    {
    }

    public RegistrationAlreadyClaimedException(string message)
        : base(message)
    {
    }

    public RegistrationAlreadyClaimedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
