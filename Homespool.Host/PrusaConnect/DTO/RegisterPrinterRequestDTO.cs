using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO;

/// <summary>
/// What a printer sends to <c>POST /p/register</c> to ask for a registration code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field is length-capped, and that is a bound rather than a formality.</b> This endpoint is
/// anonymous — it has to be, since a printer asking for its first credential has none — and each new
/// fingerprint mints a row. Uncapped, one request could store as much text as the listener accepts,
/// which is the thirty-odd megabytes nginx and Kestrel allow between them. The rate limiter
/// (<c>Program.PrinterRegistrationRateLimitPolicy</c>) bounds how many requests arrive and says
/// nothing about how large each one is, and the deployment that fills up first is an SD card.
/// </para>
/// <para>
/// <b>The caps are generous against what firmware actually sends</b>, because refusing a real printer
/// is expensive: firmware reads any non-2xx here as <c>OnlineError::Server</c> and burns one of only
/// three registration retries. The fingerprint is 50 characters in this body
/// (traced to firmware rather than guessed), a serial is
/// around twenty, and the type and firmware strings are shorter still — so each cap is a comfortable
/// multiple of the real value rather than a fit to it.
/// </para>
/// <para>
/// <b>Enforced by <c>[ApiController]</c>'s automatic model validation</b>, which answers 400 before
/// the action runs. Nothing else checks this shape, so an attribute removed here is a bound removed
/// entirely — the entity behind it is SQLite <c>TEXT</c>, which ignores length.
/// </para>
/// </remarks>
public class RegisterPrinterRequestDTO
{
    /// <summary>Longest serial number accepted. Real ones are around twenty characters.</summary>
    public const int SerialNumberMaxLength = 64;

    /// <summary>
    /// Longest fingerprint accepted. Firmware sends 50 characters here, where the headers carry
    /// only the first 16 of the same buffer.
    /// </summary>
    public const int FingerPrintMaxLength = 64;

    /// <summary>Longest printer type accepted, e.g. <c>1.3.5</c>.</summary>
    public const int PrinterTypeMaxLength = 32;

    /// <summary>Longest firmware string accepted, e.g. <c>6.4.0+11974</c>.</summary>
    public const int FirmwareMaxLength = 64;

    [JsonPropertyName("sn")]
    [StringLength(SerialNumberMaxLength)]
    public required string SerialNumber { get; set; }

    [JsonPropertyName("fingerprint")]
    [StringLength(FingerPrintMaxLength)]
    public required string FingerPrint { get; set; }

    [JsonPropertyName("printer_type")]
    [StringLength(PrinterTypeMaxLength)]
    public required string PrinterType { get; set; }

    [JsonPropertyName("firmware")]
    [StringLength(FirmwareMaxLength)]
    public required string Firmware { get; set; }
}
