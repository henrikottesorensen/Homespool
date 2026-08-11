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
public sealed record BundleOffer(
    int PrinterId,
    string? PrinterName,
    string Token,
    IReadOnlyList<PrinterAddressSuggestion> Names,
    string Snippet,
    bool TlsEnabled)
{
    /// <summary>The address selected by default: the first, which is the configured one when it is covered.</summary>
    public string? PreferredName => Names.Count > 0 ? Names[0].Value : null;

    /// <summary>
    /// Whether a bundle can be offered at all. False when the certificate carries no names — which
    /// means none has been issued, so provisioning would produce a printer that cannot connect.
    /// </summary>
    public bool CanBuild => Names.Count > 0;
}
