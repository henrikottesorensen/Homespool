namespace Homespool.Host.Http;

/// <summary>Values of the response headers <c>SecurityHeadersMiddleware</c> sets on every response.</summary>
public static class SecurityHeaderValues
{
    /// <summary><c>X-Content-Type-Options</c>: the browser may not guess a content type.</summary>
    public const string NoSniff = "nosniff";

    /// <summary><c>X-Frame-Options</c>: no page of ours may be framed.</summary>
    public const string FrameDeny = "DENY";

    /// <summary><c>Referrer-Policy</c>: the referrer goes only to our own origin.</summary>
    public const string ReferrerSameOrigin = "same-origin";
}
