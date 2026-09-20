using System;
using System.IO;
using System.Text;

namespace Homespool.Host;

/// <summary>
/// Writes a file that must not be readable by other accounts on the host, with its mode in place
/// from the moment it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writing and then narrowing leaves a window, and it is not theoretical.</b> The runtime image
/// runs at umask 0022, so a key written with <c>File.WriteAllBytes</c> and restricted on the next
/// line is 0644 for the width of that gap. <c>UnixCreateMode</c> closes it: the mode is a parameter
/// of the creation rather than a correction after it.
/// </para>
/// <para>
/// <b>The mode is applied twice and both are needed.</b> <c>UnixCreateMode</c> is honoured only
/// where the file is <i>created</i> — for one that already exists it is ignored outright and the
/// file keeps the mode it had. Every caller reaches that case: a certificate is re-minted over its
/// own key, and the writers that stage through a temporary file use a fixed name rather than a
/// random one, so an interrupted run leaves a file the next write lands on top of — and the rename
/// afterwards carries the mode along with the inode. So the explicit set is what covers those, and
/// removing it as duplication re-opens the case it exists for.
/// </para>
/// <para>
/// On Windows the mode is not expressible and the write is an ordinary one. That is the same
/// concession <c>File.SetUnixFileMode</c> makes, and the containerised deployment this protects is
/// Linux; the parameter is accepted and ignored rather than making every caller branch.
/// </para>
/// </remarks>
public static class RestrictedFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, replacing whatever was there.
    /// </summary>
    /// <param name="path">File to write. Created if absent, truncated if not.</param>
    /// <param name="contents">The bytes to store.</param>
    /// <param name="mode">The mode the file is to have, on Unix.</param>
    public static void Write(string path, byte[] contents, UnixFileMode mode)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, contents);
            return;
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = mode,
        };

        using (FileStream stream = new(path, options))
        {
            stream.Write(contents);
        }

        File.SetUnixFileMode(path, mode);
    }

    /// <summary>
    /// Writes <paramref name="contents"/> as UTF-8 without a byte order mark, which is what
    /// <c>File.WriteAllText</c> would have written and what a configuration layer reads back.
    /// </summary>
    /// <param name="path">File to write. Created if absent, truncated if not.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="mode">The mode the file is to have, on Unix.</param>
    public static void Write(string path, string contents, UnixFileMode mode)
    {
        ArgumentNullException.ThrowIfNull(contents);

        Write(path, Encoding.UTF8.GetBytes(contents), mode);
    }
}
