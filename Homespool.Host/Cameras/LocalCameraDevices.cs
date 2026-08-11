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
    private readonly UsbDeviceNames _usbNames;

    public LocalCameraDevices(ILogger<LocalCameraDevices> logger, UsbDeviceNames usbNames)
    {
        _logger = logger;
        _usbNames = usbNames;
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

    /// <summary>Whether a segment is a four-digit hex id, as udev writes vendor and product.</summary>
    private static bool IsHexId(string value)
    {
        if (value.Length != 4)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The readable name of the device a source string reads, or null if it does not read one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a camera row gets a caption without storing one. A camera's <c>Source</c>
    /// already contains the by-id name — <see cref="SourceFor"/> put it there — so the description
    /// is recoverable whenever it is wanted, and nothing has to be kept in step with the hardware.
    /// </para>
    /// <para>
    /// Null for a network camera, which has no device to name. The caller decides what to show
    /// instead; there is nothing sensible to invent here.
    /// </para>
    /// </remarks>
    public string? DescribeSource(string? source)
    {
        if (source is null)
        {
            return null;
        }

        const string VideoParameter = "video=";

        int start = source.IndexOf(VideoParameter, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += VideoParameter.Length;

        int end = source.IndexOf('&', start);
        string path = end < 0 ? source[start..] : source[start..end];

        // The last segment is the by-id name; the directory in front of it is not interesting and
        // is not assumed, so a source written by hand against by-path still names something.
        int slash = path.LastIndexOf('/');
        string deviceName = slash < 0 ? path : path[(slash + 1)..];

        return deviceName.Length == 0 ? null : Describe(deviceName);
    }

    /// <summary>
    /// Turns a udev name into something a person recognises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>udev has usually done this already.</b> A camera that reports its own strings is named
    /// with them, so <c>usb-Logitech_BRIO-video-index0</c> needs only its punctuation tidied. The
    /// numeric form is the fallback udev takes when the hardware reports nothing usable:
    /// <c>usb-046d_0821_437242E0-video-index0</c> is a real C910, and vendor, product and serial is
    /// all it will say for itself.
    /// </para>
    /// <para>
    /// <b>Only the numeric form is looked up</b>, in <see cref="UsbDeviceNames"/>. Names udev
    /// already resolved are left alone: they came from the device, and the table is a worse source
    /// for a fact the hardware stated itself.
    /// </para>
    /// <para>
    /// Best effort throughout, and udev's own name is kept alongside in
    /// <see cref="LocalCameraDevice.Name"/>, so a bad guess costs nothing.
    /// </para>
    /// </remarks>
    private string Describe(string deviceName)
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

        if (trimmed.Length == 0)
        {
            return deviceName;
        }

        return DescribeNumeric(trimmed) ?? trimmed.Replace('_', ' ');
    }

    /// <summary>
    /// Names a device whose udev name begins with a numeric vendor id, or null if it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two shapes, because udev falls back per field rather than all at once.</b> Hardware that
    /// reports neither string gives <c>046d_0821_437242E0</c> — vendor, product, serial — and both
    /// halves are looked up. Hardware that reports a product but no manufacturer gives
    /// <c>046d_HD_Pro_Webcam_C920_2A3B</c>, where only the leading id needs translating and the rest
    /// is already the device's own account of itself.
    /// </para>
    /// <para>
    /// In the first shape the serial is parenthesised, because it is the only thing telling two
    /// identical cameras apart in a picker that shows nothing else. In the second it is left in the
    /// run of words: nothing marks where the model stops and the serial starts, and guessing wrongly
    /// would cut the name in half.
    /// </para>
    /// </remarks>
    private string? DescribeNumeric(string trimmed)
    {
        string[] parts = trimmed.Split('_');

        if (parts.Length < 2 || !IsHexId(parts[0]))
        {
            return null;
        }

        if (IsHexId(parts[1]))
        {
            string? name = _usbNames.Lookup(parts[0], parts[1]);
            if (name is null)
            {
                return null;
            }

            // Anything after the product id is the serial, which may itself contain underscores.
            string serial = string.Join('_', parts[2..]);

            return serial.Length == 0 ? name : $"{name} ({serial})";
        }

        string? vendor = _usbNames.LookupVendor(parts[0]);
        if (vendor is null)
        {
            return null;
        }

        string rest = string.Join(' ', parts[1..]);

        // Some products name their maker themselves; no need to say it twice.
        return rest.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? rest : $"{vendor} {rest}";
    }
}
