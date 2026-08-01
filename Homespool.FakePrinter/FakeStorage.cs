using System;
using System.Collections.Generic;
using System.Linq;

namespace Homespool.FakePrinter;

/// <summary>
/// What is on the fake printer's <c>/usb</c>, so that <c>SEND_FILE_INFO</c> has something to report.
/// </summary>
/// <remarks>
/// <para>
/// A flat list rather than a tree, because firmware's own listing is one level deep: a
/// <c>SEND_FILE_INFO</c> on a directory enumerates <i>its</i> entries and does not recurse
/// (<c>DirRenderer</c>, render.cpp:1006-1068). Nesting is modelled by the paths themselves, which is
/// enough to answer both questions the protocol can ask - what is directly under this path, and what
/// is this one file.
/// </para>
/// <para>
/// <b>No 8.3 aliasing here either</b>, matching
/// <see cref="EventMessageBuilder.BuildFileInfo"/>'s documented deviation: there is no FAT
/// filesystem underneath and no <c>~N</c> collision index to model, so short and long names
/// coincide. Real hardware aliases nearly everything - 205 of 206 entries in a captured listing - so
/// <b>a green test against this fake says the listing is wired up correctly, not that short names
/// are handled</b>. The aliased case is covered instead by replaying a real capture, in the host's
/// <c>CaptureReplayTests</c>.
/// </para>
/// </remarks>
public sealed class FakeStorage
{
    /// <summary>The only mountpoint firmware has - <c>path_allowed</c>, planner.cpp:135-141.</summary>
    public const string Root = "/usb";

    private readonly Dictionary<string, FakeStorageEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Puts a file on the drive, replacing any entry already at that path.</summary>
    /// <remarks>
    /// Called when a transfer completes as well as from a test's own seeding, so that sending a file
    /// and then listing the drive agree with each other - which is the property that makes an
    /// end-to-end test of the listing worth anything.
    /// </remarks>
    public void AddFile(string path, long size, long modified)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _entries[path] = new FakeStorageEntry(path, size, modified, IsFolder: false);
    }

    /// <summary>Puts a directory on the drive.</summary>
    public void AddFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _entries[path] = new FakeStorageEntry(path, Size: 0, Modified: 0, IsFolder: true);
    }

    /// <summary>The entry at <paramref name="path"/>, or null when nothing is there.</summary>
    /// <remarks>
    /// <see cref="Root"/> itself is always present without being added: a printer with an empty stick
    /// still answers about <c>/usb</c>, and firmware's own emptiness is an empty <c>children</c>
    /// array rather than a refusal.
    /// </remarks>
    public FakeStorageEntry? Find(string path)
    {
        if (string.Equals(path, Root, StringComparison.Ordinal))
        {
            return new FakeStorageEntry(Root, Size: 0, Modified: 0, IsFolder: true);
        }

        return _entries.TryGetValue(path, out FakeStorageEntry? entry) ? entry : null;
    }

    /// <summary>
    /// The entries directly under <paramref name="path"/>, in the order they were added.
    /// </summary>
    /// <remarks>
    /// Direct children only. An entry at <c>/usb/sub/deep.gcode</c> is not a child of <c>/usb</c>,
    /// which is what makes this a listing rather than a search - and matches a renderer walking one
    /// <c>readdir</c>.
    /// </remarks>
    public IReadOnlyList<FakeStorageEntry> Children(string path)
    {
        string prefix = path.EndsWith('/') ? path : path + "/";

        return _entries.Values
                       .Where(entry => entry.Path.StartsWith(prefix, StringComparison.Ordinal)
                                       && !entry.Path.AsSpan(prefix.Length).Contains('/'))
                       .ToList();
    }
}
