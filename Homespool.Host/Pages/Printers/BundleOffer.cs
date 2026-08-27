using System.Collections.Generic;

using Homespool.Host.Certificates;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// Everything the download partial needs after a token has just been issued: the token itself, the
/// addresses it may be written for, and which printer it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token lives in the page and nowhere else.</b> It is PBKDF2-hashed the moment it is stored,
/// so there is no copy to fetch on a later request — which is why the download is a second POST
/// carrying it back rather than a link. TempData was considered and rejected for the same reason it
/// always is: it is the wrong place for a bearer token.
/// </para>
/// <para>
/// Nothing here is persisted or cached. When the page goes, so does the only copy, and recovering it
/// means reissuing.
/// </para>
/// </remarks>
/// <param name="PrinterId">Which printer this provisions, for the download's file name.</param>
/// <param name="PrinterName">The printer's name, or null if it was left blank.</param>
/// <param name="Token">The one-time provisioning token.</param>
/// <param name="Names">
/// Addresses the certificate covers, best first, each with what it will cost whoever picks it. Empty
/// means a bundle cannot be built.
/// </param>
/// <param name="Snippet">The ini section, rendered for <see cref="PreferredName"/>, for anyone who wants to read it.</param>
/// <param name="TlsEnabled">Whether the bundle will carry a trust anchor at all.</param>
/// <param name="LegacyPort">
/// The plaintext printer port this deployment offers, or null if it has not opened one. Null is the
/// default and means the choice below is not presented at all.
/// </param>
/// <param name="KnownFirmware">
/// What this printer last said its firmware was, or null for one that has never connected — which is
/// the ordinary case when provisioning by USB key, since the whole point is that it has not reached us
/// yet.
/// </param>
public sealed record BundleOffer(
    int PrinterId,
    string? PrinterName,
    string Token,
    IReadOnlyList<PrinterAddressSuggestion> Names,
    string Snippet,
    bool TlsEnabled,
    int? LegacyPort = null,
    string? KnownFirmware = null)
{
    /// <summary>
    /// Whether this deployment has a plaintext printer listener to offer at all.
    /// </summary>
    /// <remarks>
    /// Only ever true when TLS is on: with printer TLS off the whole deployment is already plaintext,
    /// so offering a choice between two plain listeners would be a question with one meaning and two
    /// answers.
    /// </remarks>
    public bool CanOfferLegacyEndpoint => LegacyPort is not null && TlsEnabled;

    /// <summary>
    /// Whether we know this printer could reach us over TLS — so that choosing the plaintext listener for
    /// it is a downgrade rather than a necessity.
    /// </summary>
    /// <remarks>
    /// <b>False for a printer that has never connected</b>, which is not the same as "it cannot".
    /// Nothing is inferred from ignorance here: the stronger warning is shown only when the printer
    /// itself has stated a version that carries the fix, and the version is its own claim, so this
    /// advises and never refuses.
    /// </remarks>
    public bool FirmwareCouldUseTls => PrusaConnect.PrinterFirmwareVersion.CanLoadCustomCertificate(KnownFirmware);

    /// <summary>The address selected by default: the first, which is the configured one when it is covered.</summary>
    public string? PreferredName => Names.Count > 0 ? Names[0].Value : null;

    /// <summary>
    /// Whether a bundle can be offered at all. False when the certificate carries no names — which
    /// means none has been issued, so provisioning would produce a printer that cannot connect.
    /// </summary>
    public bool CanBuild => Names.Count > 0;
}
