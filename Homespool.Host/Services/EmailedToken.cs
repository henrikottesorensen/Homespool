using System;
using System.Text;

using Microsoft.AspNetCore.WebUtilities;

namespace Homespool.Host.Services;

/// <summary>
/// Reads back a token that travelled in a link, and answers null rather than throwing when what
/// came back is not one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity's tokens are base64url-wrapped before they go into a URL</b> - a raw token contains
/// characters a query string would mangle - so every page reached from mail has to unwrap one. Five
/// do. <see cref="WebEncoders.Base64UrlDecode(string)"/> throws <see cref="FormatException"/> on
/// anything that is not valid base64url, and the code arrives from a query string, so the throw is
/// reachable by anybody: an unguarded call answers an anonymous caller with a 500.
/// </para>
/// <para>
/// <b>The ordinary cause is not an attacker, it is a mail client.</b> Long links get wrapped and
/// broken in transit routinely, so the common way to reach this is a person clicking a confirm link
/// that arrived in two pieces. Answering "that code is not usable" is both true and useful; a server
/// error is neither.
/// </para>
/// <para>
/// <b>One implementation, because there were already two and three sites with none.</b>
/// <c>Register</c> and <c>ExternalLogin</c> each carried a private copy of exactly this, while
/// <c>ConfirmEmail</c>, <c>ConfirmEmailChange</c> and <c>ResetPassword</c> called the decoder bare.
/// A guard that half the callers have is the shape a shared one exists to fix.
/// </para>
/// <para>
/// <b>Null means "not a token", not "wrong token".</b> Whether a well-formed token is valid, current
/// or belongs to this account is Identity's question, and callers still ask it - this only decides
/// whether there is anything to ask about.
/// </para>
/// </remarks>
public static class EmailedToken
{
    /// <summary>The token a link carried, or null when it cannot be one.</summary>
    /// <param name="code">The <c>code</c> query-string value, as received.</param>
    public static string? Decode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
