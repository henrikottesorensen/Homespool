using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Cameras;

/// <summary>
/// Turns a USB vendor and product id into the name printed on the box, using the system's
/// <c>usb.ids</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> udev names a video device after the strings the hardware reports,
/// and falls back to the numeric ids when it reports none. A Logitech C910 is one of the ones that
/// reports none, so its by-id name is <c>usb-046d_0821_437242E0-video-index0</c> — vendor, product,
/// serial, and nothing a person recognises. <c>usb.ids</c> is the table that closes that gap:
/// <c>046d:0821</c> is "HD Webcam C910" in it.
/// </para>
/// <para>
/// <b>Scanned per miss rather than parsed into a dictionary.</b> The file is ~700 KB and holds some
/// twenty thousand products; holding all of it to name the one or two cameras a machine has would be
/// the wrong trade on a Raspberry Pi. A lookup streams the file once, stops at the answer, and the
/// result is memoised — so the cost is paid once per distinct camera and never on a page render that
/// has already seen it. Misses are memoised too, which is what stops an unknown camera rescanning
/// the file on every request.
/// </para>
/// <para>
/// <b>A missing table is an ordinary outcome, not a fault.</b> The file ships in the image (see
/// <c>Homespool.Host/Dockerfile</c>), but a developer running on Windows or macOS has no
/// <c>/usr/share</c> at all, and an older image predates the package being added. Every path here
/// answers null in that case and the caller keeps udev's own name, which is what it displayed before
/// this class existed.
/// </para>
/// </remarks>
public sealed class UsbDeviceNames
{
    /// <summary>
    /// Where the table lives, in the order to try. The first is the real file on Debian and the
    /// second is <c>hwdata</c>'s symlink to it; listing both means this works whichever package a
    /// base image happens to carry, without asking which one it was.
    /// </summary>
    private static readonly string[] DefaultTablePaths =
    [
        "/usr/share/misc/usb.ids",
        "/usr/share/hwdata/usb.ids",
    ];

    /// <summary>
    /// Corporate suffixes that carry no information for somebody identifying a camera.
    /// </summary>
    private static readonly string[] VendorSuffixes =
    [
        " Corporation", " Corp.", " Corp", " Inc.", " Inc", " Ltd.", " Ltd", " LLC", " GmbH", " AG",
        " Co.", " Co", " S.A.", " B.V.", " A/S",
    ];

    /// <summary>
    /// Answers already found, keyed <c>vendor:product</c>. Holds nulls on purpose — see the class
    /// remarks.
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    private readonly ILogger<UsbDeviceNames> _logger;
    private readonly IReadOnlyList<string> _tablePaths;

    public UsbDeviceNames(ILogger<UsbDeviceNames> logger)
        : this(logger, DefaultTablePaths)
    {
    }

    /// <summary>
    /// Reads the table from somewhere other than the usual places.
    /// </summary>
    /// <remarks>
    /// For tests, which need a table they wrote themselves rather than whatever the build agent
    /// happens to have installed. Container builds use the parameterless constructor, which is also
    /// the one the service provider picks, since nothing registers a list of strings.
    /// </remarks>
    public UsbDeviceNames(ILogger<UsbDeviceNames> logger, IReadOnlyList<string> tablePaths)
    {
        ArgumentNullException.ThrowIfNull(tablePaths);

        _logger = logger;
        _tablePaths = tablePaths;
    }

    /// <summary>
    /// The readable name for a vendor and product id, or null when the table cannot answer.
    /// </summary>
    /// <param name="vendorId">Four hex digits, e.g. <c>046d</c>.</param>
    /// <param name="productId">Four hex digits, e.g. <c>0821</c>.</param>
    /// <returns>Something like <c>Logitech HD Webcam C910</c>, or null.</returns>
    public string? Lookup(string vendorId, string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        string key = string.Create(
            CultureInfo.InvariantCulture, $"{vendorId.ToLowerInvariant()}:{productId.ToLowerInvariant()}");

        return _cache.GetOrAdd(key, _ => Scan(vendorId, productId));
    }

    /// <summary>
    /// The maker's name for a vendor id alone, or null when the table cannot answer.
    /// </summary>
    /// <remarks>
    /// For the mixed names udev produces when hardware reports a product string but no manufacturer
    /// string: <c>usb-046d_HD_Pro_Webcam_C920_2A3B</c> already says what the thing is and needs only
    /// <c>046d</c> turned into "Logitech".
    /// </remarks>
    /// <param name="vendorId">Four hex digits, e.g. <c>046d</c>.</param>
    public string? LookupVendor(string vendorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);

        string key = string.Create(CultureInfo.InvariantCulture, $"{vendorId.ToLowerInvariant()}:");

        return _cache.GetOrAdd(key, _ => Scan(vendorId, productId: null));
    }

    /// <summary>
    /// Whether the four characters at <paramref name="start"/> are the id being looked for.
    /// </summary>
    private static bool Matches(string line, int start, string lowercaseId)
    {
        return line.Length >= start + 4
            && string.Equals(line.Substring(start, 4), lowercaseId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name following a four-digit id, which <c>usb.ids</c> separates with two spaces.
    /// </summary>
    private static string NameAfterId(string line, int start)
    {
        int from = start + 4;

        return from >= line.Length ? string.Empty : line[from..].Trim();
    }

    /// <summary>
    /// "Logitech, Inc." becomes "Logitech"; "Chicony Electronics Co., Ltd" becomes
    /// "Chicony Electronics".
    /// </summary>
    private static string TidyVendor(string vendorName)
    {
        string vendor = vendorName.Trim();

        // Everything after the first comma is the legal form in practice, and cutting there handles
        // the common "Name, Inc." and "Name Co., Ltd" shapes in one step.
        int comma = vendor.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            vendor = vendor[..comma];
        }

        // Repeated because the suffixes stack: "Acme Technology Co. Ltd" sheds two.
        bool trimmed = true;
        while (trimmed)
        {
            trimmed = false;

            foreach (string suffix in VendorSuffixes)
            {
                if (vendor.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    vendor = vendor[..^suffix.Length].TrimEnd();
                    trimmed = true;
                }
            }
        }

        return vendor.Trim();
    }

    /// <summary>
    /// Joins vendor and product the way somebody would say them out loud.
    /// </summary>
    /// <remarks>
    /// The vendor is tidied first, because <c>usb.ids</c> records legal names and nobody calls the
    /// thing a "Logitech, Inc. HD Webcam C910". Products often name the vendor themselves, so a
    /// product that already starts with it is left alone rather than made to say it twice.
    /// </remarks>
    private static string? Combine(string vendorName, string productName)
    {
        string vendor = TidyVendor(vendorName);

        if (productName.Length == 0)
        {
            return vendor.Length == 0 ? null : vendor;
        }

        if (vendor.Length == 0 || productName.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
        {
            return productName;
        }

        return $"{vendor} {productName}";
    }

    /// <summary>
    /// Reads the table looking for one vendor and, when asked, one of its products; stops as soon as
    /// it can.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="productId"/> asks for the vendor's own name, which is answered at the
    /// vendor line without reading its products at all.
    /// </remarks>
    private string? Scan(string vendorId, string? productId)
    {
        string? path = TablePath();
        if (path is null)
        {
            return null;
        }

        string vendorPrefix = vendorId.ToLowerInvariant();
        string? productPrefix = productId?.ToLowerInvariant();

        try
        {
            string? vendorName = null;

            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] != '\t')
                {
                    // A vendor line. Reaching one *after* the vendor we wanted means its block has
                    // ended without the product in it, so there is nothing further to find.
                    if (vendorName is not null)
                    {
                        return null;
                    }

                    if (Matches(line, 0, vendorPrefix))
                    {
                        vendorName = NameAfterId(line, 0);

                        // Asked for the maker alone, and the vendor line is where that is settled.
                        if (productPrefix is null)
                        {
                            string vendor = TidyVendor(vendorName);
                            return vendor.Length == 0 ? null : vendor;
                        }
                    }

                    continue;
                }

                // Indented lines before the vendor matched belong to some other vendor, and a
                // doubly-indented one is an interface rather than a product. A null productPrefix
                // has already returned at the vendor line; saying so here is what lets the compiler
                // see it too.
                if (vendorName is null || productPrefix is null || line.Length < 2 || line[1] == '\t')
                {
                    continue;
                }

                if (Matches(line, 1, productPrefix))
                {
                    return Combine(vendorName, NameAfterId(line, 1));
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable table is "no name", the same as an absent one.
            _logger.LogDebug("Could not read {Path}: {Message}", path, exception.Message);
            return null;
        }
    }

    /// <summary>The first table that exists, or null when none does.</summary>
    /// <remarks>
    /// Deliberately not memoised: it is two <c>File.Exists</c> calls, reached only on a cache miss,
    /// and caching "absent" would outlive a fix somebody applied to a running container.
    /// </remarks>
    private string? TablePath()
    {
        foreach (string candidate in _tablePaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
