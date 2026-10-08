using System;

using Homespool.Host.Firmware;

namespace Homespool.Host.Exceptions;

/// <summary>A firmware flash was not started, and says why.</summary>
public class FirmwareFlashRefusedException : Exception, ILocalisableError
{
    /// <summary>The one callers use.</summary>
    /// <param name="printerName">The printer, as the page names it.</param>
    /// <param name="refusal">Why the flash was not started.</param>
    public FirmwareFlashRefusedException(string printerName, FirmwareFlashRefusal refusal)
        : base($"Firmware flash of '{printerName}' not started: {refusal}.")
    {
        PrinterName = printerName;
        Refusal = refusal;
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public FirmwareFlashRefusedException()
    {
    }

    public FirmwareFlashRefusedException(string message)
        : base(message)
    {
    }

    public FirmwareFlashRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The printer, as the page names it.</summary>
    public string? PrinterName { get; }

    /// <summary>Why the flash was not started.</summary>
    public FirmwareFlashRefusal Refusal { get; }

    /// <inheritdoc />
    public string ResourceKey => Refusal switch
    {
        FirmwareFlashRefusal.NotConnected => "Error_FirmwareFlashNotConnected",
        FirmwareFlashRefusal.Busy => "Error_FirmwareFlashBusy",
        FirmwareFlashRefusal.Queued => "Error_FirmwareFlashQueued",
        FirmwareFlashRefusal.AlreadyFlashing => "Error_FirmwareFlashAlreadyFlashing",
        _ => "Error_FirmwareFlashNoSuchImage",
    };

    /// <inheritdoc />
    public object[] ResourceArguments => [PrinterName ?? string.Empty];
}
