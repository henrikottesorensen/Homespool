using System;

namespace PrinterService.Api.Exceptions;

public class PrinterNotFoundException : Exception
{
    public PrinterNotFoundException(string fingerPrint)
        : base($"Printer with fingerprint {fingerPrint} was not found.")
    {
    }

    public PrinterNotFoundException(string fingerPrint, Exception innerException)
        : base($"Printer with fingerprint {fingerPrint} was not found.", innerException)
    {
    }

    private PrinterNotFoundException(string message, bool _)
        : base(message)
    {
    }

    /// <summary>
    /// No registration exists for the supplied code.
    /// </summary>
    /// <remarks>
    /// The code is deliberately <b>not</b> included in the message. It is a credential - whoever holds
    /// it can claim the printer - and exception messages end up in logs. The fingerprint overloads
    /// above are for callers that actually have a fingerprint; the registration poll does not, because
    /// the printer sends only a <c>Code</c> header.
    /// </remarks>
    public static PrinterNotFoundException ForUnknownRegistrationCode() =>
        new("No printer registration matches the supplied registration code.", false);

    protected PrinterNotFoundException()
    {
    }
}
