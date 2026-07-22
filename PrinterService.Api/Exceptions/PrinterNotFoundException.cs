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

    protected PrinterNotFoundException()
    {
    }
}
