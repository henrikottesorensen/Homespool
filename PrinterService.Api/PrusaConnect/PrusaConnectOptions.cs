using System;

namespace PrinterService.Api.PrusaConnect;

/// <summary>
/// Printer-protocol tuning, bound from the <c>PrusaConnect</c> configuration section.
/// </summary>
public class PrusaConnectOptions
{
    public const string SectionName = "PrusaConnect";

    /// <summary>
    /// How long a temporary registration code stays valid, in minutes. Default one hour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A printer that posts to <c>/p/register</c> gets a code, which a user then claims. The code is
    /// renewed on the next POST once it has expired, so an expired code is a delay rather than a
    /// dead end - but the printer only retries the initial POST three times
    /// (<c>registrator.hpp</c>: <c>starting_retries = 3</c>) before giving up entirely, so this is
    /// not a value to set carelessly short.
    /// </para>
    /// <para>
    /// <b>Prusa's own servers use 24 hours</b> - the captured <c>Expires</c> header is exactly one
    /// day after the response. One hour is a deliberate divergence: a claim code is a credential
    /// for adopting a printer, and on a self-hosted LAN a shorter window is the safer default.
    /// Raise it if setup regularly spans longer than a sitting.
    /// </para>
    /// </remarks>
    public int RegistrationCodeLifetimeMinutes { get; set; } = 60;

    /// <summary><see cref="RegistrationCodeLifetimeMinutes"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RegistrationCodeLifetime => TimeSpan.FromMinutes(RegistrationCodeLifetimeMinutes);
}
