using System;

namespace PrinterService.Host.Exceptions;

/// <summary>
/// The registration named by a claim's code already has a <c>Printer</c> attached.
/// </summary>
/// <remarks>
/// The concrete answer to the "concurrent claim of the same code" loose end left open in
/// AGENT-NOTES phase-1.5 §15 step 7: the first claim wins and every later one is rejected, rather
/// than silently overwriting the printer the first claim created.
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
