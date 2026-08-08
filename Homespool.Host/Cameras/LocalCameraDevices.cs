using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Cameras;

/// <summary>
/// Lists the video devices attached to this machine, so a camera can be picked rather than typed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads names, and holds no capability to use them.</b> <c>/dev/v4l/by-id</c> is bind-mounted
/// read-only into this container while the devices themselves are passed only to the stream
/// server — so the symlinks here are listable and their targets deliberately do not resolve
/// (verified 2026-08-08: <c>/dev/video0</c> is absent inside the container while the by-id entry is
/// present). Naming a camera and being able to open it are separated on purpose.
/// </para>
/// <para>
/// <b>By id rather than by node.</b> <c>/dev/video0</c> is whichever camera enumerated first: on
/// this board, one device took that node from another within a single session. A by-id name carries
/// the model and, where the hardware provides one, the serial - so it survives a reboot and a
/// replug. Cheap cameras report no serial, and two identical ones would collide; <c>by-path</c> is
/// the fallback there, at the cost of changing when the plug moves.
/// </para>
/// <para>
/// An empty list is the ordinary case. Most deployments have no camera attached, and the mount
/// itself is created empty by Docker when the host has none - so "nothing here" must read as
/// "nothing attached", never as an error.
/// </para>
/// </remarks>
public sealed class LocalCameraDevices
{
    /// <summary>
    /// Where udev keeps the stable names. Not configurable: this is the path passed through from the
    /// host, and it is the same on every Linux the appliance runs on.
    /// </summary>
    private const string ByIdDirectory = "/dev/v4l/by-id";

    /// <summary>
    /// Only capture nodes. UVC exposes a metadata node beside every camera - index1 alongside
    /// index0 - which is a real device and useless as a picture.
    /// </summary>
    private const string CaptureSuffix = "-video-index0";

    private readonly ILogger<LocalCameraDevices> _logger;

    public LocalCameraDevices(ILogger<LocalCameraDevices> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The source string that reads this device, with the format stated rather than inherited.
    /// </summary>
    /// <remarks>
    /// <b><c>input_format=mjpeg</c> is load-bearing.</b> Cameras disagree about which format they
    /// list first, and the one they list first is what a caller gets by default - measured
    /// 2026-08-08, a cheap camera offered MJPEG first and a better one offered uncompressed first.
    /// Taking the default silently costs a transcode per frame on every camera of the second kind,
    /// and produces a different image at the encoder's own quality. Same command, same board,
    /// invisibly worse.
    /// </remarks>
    public static string SourceFor(string deviceName, int width = 1280, int height = 720)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        return $"ffmpeg:device?video={ByIdDirectory}/{deviceName}"
             + $"&input_format=mjpeg&video_size={width}x{height}";
    }

    /// <summary>
    /// The capture devices attached to this machine, newest listing each time.
    /// </summary>
    /// <remarks>
    /// Not cached: a camera can be plugged in while the page is open, and the directory listing is
    /// a handful of entries.
    /// </remarks>
    public IReadOnlyList<LocalCameraDevice> List()
    {
        try
        {
            if (!Directory.Exists(ByIdDirectory))
            {
                return [];
            }

            return Directory.EnumerateFileSystemEntries(ByIdDirectory)
                            .Select(Path.GetFileName)
                            .Where(name => name is not null && name.EndsWith(CaptureSuffix, StringComparison.Ordinal))
                            .Select(name => new LocalCameraDevice(name!, Describe(name!)))
                            .OrderBy(device => device.Description, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable mount is "no cameras", not a failure worth showing anybody.
            _logger.LogDebug("Could not list local video devices: {Message}", exception.Message);
            return [];
        }
    }

    /// <summary>
    /// Turns a udev name into something a person recognises.
    /// </summary>
    /// <remarks>
    /// Best effort and deliberately not clever: udev's own name is kept alongside, so a bad guess
    /// costs nothing. <c>usb-046d_0821_437242E0-video-index0</c> reads as <c>046d_0821_437242E0</c>,
    /// which is vendor, product and serial - enough to tell two cameras apart, which is all this has
    /// to do.
    /// </remarks>
    private static string Describe(string deviceName)
    {
        string trimmed = deviceName;

        if (trimmed.StartsWith("usb-", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        if (trimmed.EndsWith(CaptureSuffix, StringComparison.Ordinal))
        {
            trimmed = trimmed[..^CaptureSuffix.Length];
        }

        return trimmed.Length == 0 ? deviceName : trimmed.Replace('_', ' ');
    }
}
