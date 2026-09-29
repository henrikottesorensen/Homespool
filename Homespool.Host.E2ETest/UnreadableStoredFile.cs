using System;
using System.IO;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Makes one of a user's stored files impossible to open while leaving it listed - a delete racing
/// a send, as the transfer offer sees it, made deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store finds a file by listing its directory and never opens it</b>, so a file with no
/// permissions is still found, sized and indexed. The transfer offer is the first thing to open it,
/// and that is where the refusal under test is raised.
/// </para>
/// <para>
/// <b>Root reads through any mode, and Windows has none to take away</b>, so where this process can
/// still open the file the test is skipped rather than allowed to pass or fail for a reason it is not
/// about.
/// </para>
/// </remarks>
internal static class UnreadableStoredFile
{
    /// <summary>Strips every permission from <paramref name="userId"/>'s file <paramref name="name"/>.</summary>
    public static void Make(HomespoolFactory factory, long userId, string name)
    {
        string path;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            path = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>().FindForPrinting(userId, name)!.Path;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        Assert.SkipWhen(CanOpen(path),
                        "The file still opens: this is Windows, which has no Unix mode to take away, or the process is root, which reads through one.");
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
