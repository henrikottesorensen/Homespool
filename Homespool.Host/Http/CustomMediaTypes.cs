namespace Homespool.Host.Http;

/// <summary>Media types that <c>MediaTypeNames</c> does not have.</summary>
public static class CustomMediaTypes
{
    /// <summary>An MJPEG stream: one JPEG per part, each replacing the last.</summary>
    public const string MultipartMixedReplace = "multipart/x-mixed-replace";
}
