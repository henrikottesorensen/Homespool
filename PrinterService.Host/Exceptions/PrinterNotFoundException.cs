using System;

namespace PrinterService.Host.Exceptions;

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

    /// <param name="message">The complete exception message, used verbatim.</param>
    /// <param name="literalMessage">
    /// Ignored. It exists only to give this overload a signature distinct from
    /// <see cref="PrinterNotFoundException(string)"/>, which treats its argument as a fingerprint and
    /// builds a message around it. Callers pass <c>true</c> because that is what it asserts.
    /// </param>
    private PrinterNotFoundException(string message, bool literalMessage)
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
        new("No printer registration matches the supplied registration code.", literalMessage: true);

    /// <summary>No printer exists with the given id. Distinct from the fingerprint overloads above,
    /// which are for callers on the registration path that never have an id.</summary>
    public static PrinterNotFoundException ForId(int printerId) =>
        new($"Printer {printerId} was not found.", literalMessage: true);

    protected PrinterNotFoundException()
    {
    }
}
