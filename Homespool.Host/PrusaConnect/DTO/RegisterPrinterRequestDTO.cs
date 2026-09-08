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
/// (<c>RateLimitPolicies.PrinterRegistrationStart</c>) bounds how many requests arrive and says
/// nothing about how large each one is, and the deployment that fills up first is an SD card.
/// </para>
/// <para>
/// <b>The numbers are <see cref="PrusaConnectConstants"/>, not this type's own</b>, because a printer
/// states these same fields again on every <c>INFO</c> and both messages answer to one bound. Bounding
/// this endpoint alone achieves nothing: a value refused here can be stated a second later over
/// <c>INFO</c> and stored. That type carries the argument, and the fact that the caps are generous
/// against what firmware really sends.
/// </para>
/// <para>
/// <b>Enforced by <c>[ApiController]</c>'s automatic model validation</b>, which answers 400 before
/// the action runs. Nothing else checks this shape, so an attribute removed here is a bound removed
/// entirely — the entity behind it is SQLite <c>TEXT</c>, which ignores length. <b>Refusing the whole
/// message is right here and wrong on the <c>INFO</c> path</b>, which drops the one bad field instead:
/// a registration is a single fact and an <c>INFO</c> is several.
/// </para>
/// </remarks>
public class RegisterPrinterRequestDTO
{
    [JsonPropertyName("sn")]
    [StringLength(PrusaConnectConstants.SerialNumberMaxLength)]
    public required string SerialNumber { get; set; }

    [JsonPropertyName("fingerprint")]
    [StringLength(PrusaConnectConstants.FingerPrintMaxLength)]
    public required string FingerPrint { get; set; }

    [JsonPropertyName("printer_type")]
    [StringLength(PrusaConnectConstants.PrinterTypeMaxLength)]
    public required string PrinterType { get; set; }

    [JsonPropertyName("firmware")]
    [StringLength(PrusaConnectConstants.FirmwareMaxLength)]
    public required string Firmware { get; set; }
}
