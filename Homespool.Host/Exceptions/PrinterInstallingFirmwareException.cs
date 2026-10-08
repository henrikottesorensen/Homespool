using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// Firmware is being installed on the printer, so it is not sent anything else until that is done.
/// </summary>
/// <remarks>
/// The install ends in the printer restarting, which a transfer under way would not survive, and its
/// own send needs the printer's one transfer slot.
/// </remarks>
public class PrinterInstallingFirmwareException : Exception, ILocalisableError
{
    /// <summary>The one callers use.</summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="printerName">The printer, as people know it.</param>
    public PrinterInstallingFirmwareException(int printerId, string printerName)
        : base($"Firmware is being installed on printer {printerId}; send files to it once it is back.")
    {
        PrinterName = printerName;
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public PrinterInstallingFirmwareException()
    {
    }

    public PrinterInstallingFirmwareException(string message)
        : base(message)
    {
    }

    public PrinterInstallingFirmwareException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The printer, as people know it.</summary>
    public string? PrinterName { get; }

    /// <inheritdoc />
    public string ResourceKey => "Error_PrinterInstallingFirmware";

    /// <inheritdoc />
    public object[] ResourceArguments => [PrinterName ?? string.Empty];
}
