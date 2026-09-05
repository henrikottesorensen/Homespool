using System.Globalization;

using Microsoft.Extensions.Options;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Refuses a printer host longer than a printer can hold, at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refusing to start is the right severity.</b> Every bundle written from a long name is a printer
/// that cannot connect, the failure surfaces at the printer's panel as a connection error naming
/// nothing, and the operator who set the value is the one reading the log at the boot right after
/// they set it. <see cref="PrusaConnectOptions.PrinterHostMaxLength"/> carries the firmware facts.
/// </para>
/// <para>
/// One sentence, produced in one place, because the same refusal is reported by the certificate
/// health check for a deployment that predates this validation and by the bundle builder for a name
/// that never went through the options at all.
/// </para>
/// </remarks>
public sealed class PrinterHostLengthValidator : IValidateOptions<PrusaConnectOptions>
{
    /// <summary>
    /// Why <paramref name="host"/> cannot be a printer host, or null when it can.
    /// </summary>
    /// <param name="host">A configured or typed printer host; whitespace around it does not count.</param>
    public static string? Refusal(string? host)
    {
        string trimmed = host?.Trim() ?? string.Empty;

        if (trimmed.Length <= PrusaConnectOptions.PrinterHostMaxLength)
        {
            return null;
        }

        return $"The printer host '{trimmed}' is {trimmed.Length.ToString(CultureInfo.InvariantCulture)} characters, and Prusa "
               + $"firmware stores the Connect hostname in a {PrusaConnectOptions.PrinterHostMaxLength.ToString(CultureInfo.InvariantCulture)}-character "
               + $"field - silently truncated, so a printer given this name would dial '{trimmed[..PrusaConnectOptions.PrinterHostMaxLength]}' "
               + "and never connect. Use a shorter name, or this machine's address, which always fits.";
    }

    public ValidateOptionsResult Validate(string? name, PrusaConnectOptions options)
    {
        string? refusal = Refusal(options?.PrinterHost);

        return refusal is null ?
            ValidateOptionsResult.Success :
            ValidateOptionsResult.Fail($"PrusaConnect:PrinterHost (PRINTER_HOST) is unusable. {refusal}");
    }
}
