using System.Diagnostics.CodeAnalysis;

using Homespool.Host.Services;

namespace Homespool.Host.Accounts;

/// <summary>
/// What an email address has to be before this server keeps it: every character one a person can
/// see, and no longer than a mail server could be handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>About the string, and only the string.</b> The framework's <c>[EmailAddress]</c> checks the
/// shape and refuses a line break; an escape sequence, a right-to-left override, a zero-width space
/// and a five-thousand-character local part all pass it. A kept address is shown - to an
/// administrator, in a mail header, beside an invitation - so it is held to what
/// <see cref="PrintableText"/> holds any shown value to.
/// </para>
/// <para>
/// <b>Nothing is resolved.</b> This server never connects to an address's domain; the configured
/// relay does, later, with its own resolver, so a lookup here would be a claim about one network
/// made on behalf of another. It would also refuse a good address on a box with no way out yet,
/// which is what first-time setup often is. Whether an address works is proved by the mail sent to
/// it being answered, and that is already how an address is confirmed.
/// </para>
/// </remarks>
public static class EmailAddresses
{
    /// <summary>The longest address a mail server can be handed, from SMTP's limit on a path.</summary>
    public const int MaxLength = 254;

    /// <summary>Whether <paramref name="address"/> is one to keep. Says nothing about its shape.</summary>
    public static bool IsStorable([NotNullWhen(true)] string? address)
    {
        return !string.IsNullOrEmpty(address) &&
               address.Length <= MaxLength &&
               PrintableText.IsPrintable(address);
    }
}
