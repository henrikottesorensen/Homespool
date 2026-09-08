using System.Buffers.Text;
using System.Security.Cryptography;

namespace Homespool.Host.Middleware;

/// <summary>
/// The one value that lets an inline script run under this response's Content-Security-Policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped, and that is the whole design.</b> A nonce is worth exactly as much as its
/// unpredictability per response: <see cref="SecurityHeadersMiddleware"/> puts this value in the
/// policy header and a view puts the same value on its <c>&lt;script&gt;</c>, and a browser runs the
/// script only if the two agree. Because the container makes one of these per request, both readers
/// see the same fresh value and nothing has to be threaded through <c>HttpContext.Items</c> by name.
/// </para>
/// <para>
/// <b>128 bits from the CSPRNG</b>, which is what the specification asks for, in the URL-safe base64
/// alphabet. The header grammar admits that alphabet, and the reason to prefer it is the attribute:
/// Razor encodes <c>+</c> as <c>&amp;#x2B;</c> inside a quoted attribute, which a browser decodes
/// back before comparing, but which makes the page's text differ from the header's - and a test
/// that compares the two, or two pages to each other, wants the value written once, the same way.
/// </para>
/// </remarks>
public sealed class CspNonce
{
    public CspNonce()
    {
        Value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The nonce, ready to place after <c>nonce-</c> in the header and in the attribute.</summary>
    public string Value { get; }
}
