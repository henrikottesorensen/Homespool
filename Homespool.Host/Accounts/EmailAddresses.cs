using System;
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

    /// <summary>
    /// Whether <paramref name="first"/> and <paramref name="second"/> are the same address, ignoring
    /// case - in any script, but only case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both folds have to agree, and that is the whole trick.</b> A single fold does more than case:
    /// <see cref="string.ToUpperInvariant"/> maps a long s (U+017F) to S, and
    /// <see cref="string.ToLowerInvariant"/> maps the kelvin sign (U+212A) to k, so either one alone lets
    /// a look-alike mailbox meet an address it is not. Each of those mappings goes one way only - the
    /// long s lowercases to itself, the kelvin sign uppercases to itself - so asking for both refuses
    /// them while still meeting every genuine pair: an exhaustive run over every scalar value finds no
    /// character outside ASCII that matches one inside it this way, and finds å and Å, æ and Æ, ø and Ø
    /// matching as they should.
    /// </para>
    /// <para>
    /// <b>No NFC, on purpose.</b> NFC maps the kelvin sign to a plain K, which both folds then agree
    /// with, so normalising first would let back in exactly what this refuses. The cost is that an
    /// address sent with a combining ring does not meet the same address with a precomposed å. An
    /// address is normally written precomposed, so the cost is expected to be narrow - expected, not
    /// measured against any provider.
    /// </para>
    /// <para>
    /// <b>In C#, never in SQL.</b> SQLite's <c>upper()</c> and <c>lower()</c> fold a-z and nothing
    /// else, so a comparison translated there cannot see this rule at all.
    /// </para>
    /// </remarks>
    public static bool SameAddress(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return string.Equals(first.ToUpperInvariant(), second.ToUpperInvariant(), StringComparison.Ordinal) &&
               string.Equals(first.ToLowerInvariant(), second.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
