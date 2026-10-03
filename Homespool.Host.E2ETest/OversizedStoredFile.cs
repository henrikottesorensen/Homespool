using System.IO;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Makes a stored file too large for a printer to be sent without writing it: a sparse file of 4 GiB
/// costs the disk nothing, and the size is all a send looks at.
/// </summary>
internal static class OversizedStoredFile
{
    /// <summary>Stretches <paramref name="userId"/>'s file <paramref name="name"/> to the ceiling.</summary>
    public static void Make(HomespoolFactory factory, long userId, string name)
    {
        string path;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            path = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>().FindForPrinting(userId, name)!.Path;
        }

        using FileStream stream = File.OpenWrite(path);
        stream.SetLength(uint.MaxValue);
    }
}
