using System;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Printer-protocol tuning, bound from the <c>PrusaConnect</c> configuration section.
/// </summary>
public class PrusaConnectOptions
{
    public const string SectionName = "PrusaConnect";

    /// <summary>
    /// How long a temporary registration code stays valid, in minutes. Default 30.
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
    /// day after the response. 30 minutes is a deliberate divergence: a claim code is a credential
    /// for adopting a printer, and on a self-hosted deployment a shorter window is the safer
    /// default. It is also half the exposure the code's own entropy is sized against
    /// (<see cref="CodeGenerator"/>), since the window is what bounds an online guessing attempt.
    /// Claiming is something done while standing at the printer, so 30 minutes is generous for the
    /// real workflow; raise it if setup regularly spans longer than a sitting.
    /// </para>
    /// </remarks>
    public int RegistrationCodeLifetimeMinutes { get; set; } = 30;

    /// <summary>
    /// How many consecutive unrecognised registration codes an account may submit before it is
    /// backed off. Default 5.
    /// </summary>
    /// <remarks>
    /// The same figure Identity's own login lockout uses, for the same reason: it is comfortably
    /// above what fat fingers produce and far below what guessing needs.
    /// </remarks>
    public int MaxFailedClaimAttempts { get; set; } = 5;

    /// <summary>
    /// The first backoff applied once <see cref="MaxFailedClaimAttempts"/> is passed, in seconds.
    /// Doubles per further failure, up to <see cref="ClaimLockoutMaxSeconds"/>. Default 30.
    /// </summary>
    public int ClaimLockoutBaseSeconds { get; set; } = 30;

    /// <summary>
    /// The ceiling on the exponential backoff, in seconds. Default one hour.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than unbounded doubling, because the backoff must always self-heal: the
    /// person locked out is overwhelmingly likely to be a legitimate user who mistyped, and a claim
    /// code expires inside <see cref="RegistrationCodeLifetimeMinutes"/> anyway.
    /// </remarks>
    public int ClaimLockoutMaxSeconds { get; set; } = 3600;

    /// <summary><see cref="RegistrationCodeLifetimeMinutes"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RegistrationCodeLifetime => TimeSpan.FromMinutes(RegistrationCodeLifetimeMinutes);

    /// <summary>
    /// The hostname a <b>printer</b> should be pointed at to reach this server — the <c>hostname</c>
    /// value in the <c>[service::connect]</c> section of a USB-key <c>prusa_printer_settings.ini</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default: there is no way to infer a self-hosted server's externally-reachable name from
    /// inside the process (it sits behind whatever DNS, reverse proxy or LAN address the operator
    /// chose). The provisioning UI cannot produce a usable snippet until this is set, and says so.
    /// This is the server's own address, deliberately separate from any Prusa host.
    /// </para>
    /// <para>
    /// <b>Printer-facing only, and named so since 2026-07-29.</b> It was <c>PublicHost</c>, which read
    /// as "the address of this deployment" and is not what it is: every consumer is the printer's ini
    /// (<see cref="ConnectIni"/> and the page that renders it). Nothing user-facing reads it —
    /// absolute URLs in mail come from <c>Url.Page(..., protocol: Request.Scheme)</c>, i.e. from the
    /// incoming request, so the user-facing address is never configured at all. The rename matters
    /// because the printer address is about to stop being the same thing as the user address: they get
    /// separate listeners and separate certificates (<c>notes/tls-by-default.md</c>).
    /// </para>
    /// </remarks>
    public string PrinterHost { get; set; } = string.Empty;

    /// <summary>
    /// The port a <b>printer</b> is told to dial — the <c>port</c> line in a provisioning snippet's
    /// <c>[service::connect]</c> section.
    /// </summary>
    /// <remarks>
    /// Prusa's firmware defaults <c>connect_port</c> to 443, and this deployment deliberately does
    /// not: 443 belongs to the people-facing proxy, and the two audiences are kept on separate ports
    /// with separate certificates. This is the host side of the printer port mapping — what a printer
    /// dials from outside — not <see cref="Listeners.ListenerOptions.PrinterPort"/>, which is the
    /// plaintext port nginx forwards to inside the deployment. The two happen to share a number in
    /// <c>compose.yaml</c>; nothing requires them to.
    /// </remarks>
    public int PrinterPort { get; set; } = 15443;

    /// <summary>
    /// Whether printers reach this server over TLS. On by default, as the firmware is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One setting, both ends.</b> It is the <c>tls</c> key written into a printer's ini
    /// (<see cref="ConnectIni"/>) <i>and</i> whether a leaf is minted for nginx to present
    /// (<c>Program.EnsurePrinterCertificate</c>), so the two cannot disagree about whether the
    /// printer path is encrypted.
    /// </para>
    /// <para>
    /// <b>It does not decide what this process binds.</b> <c>Program.ConfigureListeners</c> listens on
    /// <c>Listeners:PrinterPort</c> as plain HTTP unconditionally, with no branch on this flag: nginx
    /// terminates the printer's TLS in front of it, because the record size the firmware needs is not
    /// something <c>SslStream</c> can be made to emit. What this switches is therefore what sits in
    /// front of that port — the proxy with a certificate, or nothing — which is
    /// <c>compose.yaml</c>'s business rather than this process's.
    /// </para>
    /// <para>
    /// <b>Turning it off is a testing tool, not a deployment option.</b> The printer's token then
    /// crosses the network in clear in both directions: the one on the USB stick and the one issued at
    /// claim. That is precisely what reading the protocol on the wire requires — a capture of the TLS
    /// listener is ciphertext — and precisely what a household network does not want. Startup says so
    /// every time, and no certificate is issued while it is off.
    /// </para>
    /// <para>
    /// It still says nothing about whether the web UI is served over TLS: that is
    /// <c>Listeners:UserHttpsPort</c>, or the proxy in front of <c>Listeners:UserPort</c>.
    /// </para>
    /// </remarks>
    public bool PrinterTls { get; set; } = true;

    /// <summary>True once <see cref="PrinterHost"/> has been set, i.e. a provisioning snippet can be produced.</summary>
    public bool IsPrinterAddressConfigured => !string.IsNullOrWhiteSpace(PrinterHost);

    /// <summary>
    /// How long <see cref="PrinterConnectionActor"/> waits for a printer's reply (a
    /// <c>Finished</c>/<c>Rejected</c>/<c>StateChanged</c> event echoing the command's id) before
    /// giving up. Default 10 seconds.
    /// </summary>
    /// <remarks>
    /// The firmware answers essentially immediately for the commands this pass sends (see
    /// <c>planner.cpp:667-790</c> at the pinned ref) - this mostly guards against a printer that
    /// goes quiet mid-command (e.g. drops off the network) rather than genuine processing latency.
    /// </remarks>
    public double CommandResponseTimeoutSeconds { get; set; } = 10;

    /// <summary><see cref="CommandResponseTimeoutSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan CommandResponseTimeout => TimeSpan.FromSeconds(CommandResponseTimeoutSeconds);
}
