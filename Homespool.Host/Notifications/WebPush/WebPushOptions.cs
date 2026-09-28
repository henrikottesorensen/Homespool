using System;
using System.Collections.Generic;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Web Push settings, under <c>Notifications:WebPush</c>. Both have working defaults; neither needs
/// setting for browser notifications to work.
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "Notifications:WebPush";

    /// <summary>
    /// Where the default <see cref="Contact"/> points: this software's source, since the application is
    /// never told who runs it.
    /// </summary>
    public const string DefaultContact = "https://github.com/henrikottesorensen/Homespool";

    /// <summary>
    /// How a push service can reach whoever sends these requests, as a <c>mailto:</c> or an
    /// <c>https:</c> address - RFC 8292's <c>sub</c> claim, sent to the push services with every
    /// request.
    /// </summary>
    /// <remarks>
    /// <b>Not the administrator's address by default</b>, although that would be the natural contact:
    /// it would hand an address from this deployment to Google, Mozilla, Apple and Microsoft on every
    /// notification, and nobody set this up to do that. An operator who wants to be reachable by them
    /// sets it.
    /// </remarks>
    public string Contact { get; set; } = DefaultContact;

    /// <summary>
    /// Push service hosts accepted beside the ones Homespool knows, for a browser whose vendor runs its
    /// own. <c>*.example.net</c> accepts any name under <c>example.net</c> and not the name itself.
    /// </summary>
    public IList<string> AdditionalEndpointHosts { get; set; } = [];

    /// <summary>Whether <paramref name="contact"/> is a form RFC 8292 accepts.</summary>
    public static bool IsValidContact(string? contact)
    {
        if (string.IsNullOrWhiteSpace(contact))
        {
            return false;
        }

        if (contact.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return contact.Length > "mailto:".Length;
        }

        return Uri.TryCreate(contact, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme == Uri.UriSchemeHttps;
    }
}
