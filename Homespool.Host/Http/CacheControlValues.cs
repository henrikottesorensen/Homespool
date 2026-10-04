namespace Homespool.Host.Http;

/// <summary>
/// The <c>Cache-Control</c> values we send. The directive names are spelled once, here;
/// <c>CacheControlHeaderValue</c>'s own are static readonly rather than constant, so they cannot be
/// composed into constants.
/// </summary>
public static class CacheControlValues
{
    /// <summary>The <c>no-cache</c> directive, also the value of the legacy <c>Pragma</c> header.</summary>
    public const string NoCache = "no-cache";

    /// <summary>The response is not kept anywhere.</summary>
    public const string NoStore = "no-store";

    private const string MustRevalidate = "must-revalidate";

    /// <summary>For a response carrying a secret that was shown once: <c>no-cache, no-store</c>.</summary>
    public const string NoCacheNoStore = $"{NoCache}, {NoStore}";

    /// <summary>The same two directives in the order the health and camera responses send them.</summary>
    public const string NoStoreNoCache = $"{NoStore}, {NoCache}";

    /// <summary>For a live camera response that must not be reused even after revalidation.</summary>
    public const string NoStoreNoCacheMustRevalidate = $"{NoStore}, {NoCache}, {MustRevalidate}";

    /// <summary>A response only the requesting browser may keep, for one day.</summary>
    public const string PrivateOneDay = "private, max-age=86400";
}
