using System;

using Homespool.Host.Firmware;

namespace Homespool.Host.Exceptions;

/// <summary>An upload was not stored as a firmware image, and says why.</summary>
/// <remarks>
/// <b>Each refusal names its remedy</b>, because they have different ones: a damaged download is
/// fetched again, a local build is not accepted at all, and an image for another printer belongs on
/// that printer's page.
/// </remarks>
public class FirmwareImageRefusedException : Exception, ILocalisableError
{
    /// <summary>The one callers use.</summary>
    /// <param name="fileName">The name the image was uploaded under.</param>
    /// <param name="refusal">Why it was refused.</param>
    /// <param name="check">What the verifier found, when it got that far.</param>
    /// <param name="printerName">The printer it was offered to, as the page names it.</param>
    public FirmwareImageRefusedException(string fileName,
                                         FirmwareImageRefusal refusal,
                                         PrusaFirmwareCheck? check = null,
                                         string? printerName = null)
        : base($"'{fileName}' was not stored as a firmware image: {refusal} ({check?.Verdict}).")
    {
        FileName = fileName;
        Refusal = refusal;
        Check = check;
        PrinterName = printerName;
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public FirmwareImageRefusedException()
    {
    }

    public FirmwareImageRefusedException(string message)
        : base(message)
    {
    }

    public FirmwareImageRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The name the image was uploaded under.</summary>
    public string? FileName { get; }

    /// <summary>Why it was refused.</summary>
    public FirmwareImageRefusal Refusal { get; }

    /// <summary>What the verifier found, when it got that far.</summary>
    public PrusaFirmwareCheck? Check { get; }

    /// <summary>The printer it was offered to, as the page names it.</summary>
    public string? PrinterName { get; }

    /// <inheritdoc />
    public string ResourceKey => Refusal switch
    {
        FirmwareImageRefusal.NotAnImageName => "Error_FirmwareNotAnImageName",
        FirmwareImageRefusal.WrongPrinter => "Error_FirmwareWrongPrinter",
        FirmwareImageRefusal.PrinterModelUnknown => "Error_FirmwarePrinterModelUnknown",
        FirmwareImageRefusal.NameTaken => "Error_FirmwareNameTaken",
        FirmwareImageRefusal.NotVerified => Check?.Verdict switch
        {
            PrusaFirmwareVerdict.Truncated => "Error_FirmwareTruncated",
            PrusaFirmwareVerdict.Damaged => "Error_FirmwareDamaged",
            PrusaFirmwareVerdict.NoSignature => "Error_FirmwareNoSignature",
            PrusaFirmwareVerdict.SignatureInvalid => "Error_FirmwareNotPrusas",
            PrusaFirmwareVerdict.ResourcesUnreadable => "Error_FirmwareResourcesUnreadable",
            PrusaFirmwareVerdict.ResourcesChanged => "Error_FirmwareResourcesChanged",
            _ => "Error_FirmwareNotAnImage",
        },
        _ => "Error_FirmwareNotAnImage",
    };

    /// <inheritdoc />
    public object[] ResourceArguments => [FileName ?? string.Empty, Check?.Header?.Version ?? string.Empty, PrinterName ?? string.Empty];
}
