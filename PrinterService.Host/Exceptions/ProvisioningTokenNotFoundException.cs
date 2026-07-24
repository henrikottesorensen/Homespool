using System;

namespace PrinterService.Host.Exceptions;

/// <summary>
/// Thrown when a printer has no <em>outstanding</em> USB-key provisioning token to regenerate — either
/// none was ever created for it, or the one that was has already been bound to a printer at first
/// contact (at which point the printer is enrolled and its credential has moved to
/// <c>PrusaConnectAuthenticationData</c>).
/// </summary>
public class ProvisioningTokenNotFoundException : Exception
{
    public ProvisioningTokenNotFoundException()
        : base("No outstanding provisioning token exists for this printer.")
    {
    }

    public ProvisioningTokenNotFoundException(string message)
        : base(message)
    {
    }

    public ProvisioningTokenNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
