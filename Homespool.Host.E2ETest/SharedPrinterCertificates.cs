using System;
using System.IO;
using System.Threading;

using Homespool.Host.Certificates;

namespace Homespool.Host.E2ETest;

/// <summary>
/// One printer authority for the whole assembly, copied into each test host's content root before it
/// starts instead of minted inside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minting is expensive on purpose.</b> The authority's key is stored encrypted under a PBKDF2 work
/// factor chosen to defend a typed passphrase, and a mint spends it three times - once writing the
/// key, once proving the written file opens, once loading the pair back. Roughly 1.2 seconds, which is
/// the right price for a deployment that pays it once and the wrong one for a suite that paid it 312
/// times: every test class builds its factory in <c>InitializeAsync</c>, which xUnit runs per
/// <i>test</i>, and every host gets a fresh content root. It was 39% of the run.
/// </para>
/// <para>
/// <b>The fix is the number of mints, not the cost of one.</b> Lowering the work factor for tests
/// would trade a real property of the deployment for a faster suite, and would leave the suite still
/// doing 312 of something it needs once.
/// </para>
/// <para>
/// <b>The first host mints normally and then donates what it produced.</b> Donation rather than a
/// hand-built template is deliberate: the files are by construction exactly what a host on this
/// machine would have written, including the leaf's names, so nothing here restates how those are
/// chosen and then drifts from it. It donates the two <i>relative</i> directories along with the
/// files, for the same reason - the paths come from the configuration a host actually resolved rather
/// than from literals repeated here.
/// </para>
/// <para>
/// <b>Planting happens before the host is built, and that is the load-bearing part.</b>
/// <c>Program.EnsurePrinterCertificate</c> runs on the startup path under
/// <c>WebApplicationFactory</c> exactly as it does in production, so anything done after
/// <c>base.CreateHost</c> returns is too late: the host has already minted. Planting late looks like
/// it works - the files end up correct and every test passes - it just saves nothing.
/// </para>
/// <para>
/// <b>Isolation is unchanged, and that is the constraint that shaped this.</b> Each host still owns
/// its own files under its own content root - these are copies, not a shared directory - so the
/// relative-path guard in <see cref="ContentRootIsolationTests"/> still holds, and that test is what
/// fails loudly if a certificate directory ever escapes the content root and quietly disables the
/// sharing here. Pointing every host at one absolute certificate directory would have been simpler,
/// and is exactly what that test exists to forbid: certificates escaping into the developer's own
/// <c>Homespool.Host/data</c> is a bug this suite has already had once, and it passed while measuring
/// the wrong machine.
/// </para>
/// <para>
/// Because the donor is whichever host got there first, a few hosts racing at the start of a run may
/// each mint before any template exists. That is a handful, not 312, and no test varies the inputs a
/// certificate is built from - <see cref="HomespoolFactory.PrinterHost"/> is a constant and the
/// address resolver is the real one - so which host wins does not change what the others receive.
/// </para>
/// </remarks>
internal static class SharedPrinterCertificates
{
    private const string AuthorityFolder = "authority";
    private const string ProxyFolder = "proxy";

    private static readonly Lock Gate = new();

    private static string? _template;
    private static string? _authorityDirectory;
    private static string? _proxyDirectory;

    /// <summary>
    /// Copies the donated certificates into a content root, if some host has donated yet. Call this
    /// <em>before</em> the host is built - see the remarks on this class.
    /// </summary>
    /// <returns>Whether anything was planted; false means this host is about to mint.</returns>
    public static bool Plant(string contentRoot)
    {
        lock (Gate)
        {
            if (_template is null)
            {
                return false;
            }

            CopyInto(Path.Combine(_template, AuthorityFolder), Path.Combine(contentRoot, _authorityDirectory!));
            CopyInto(Path.Combine(_template, ProxyFolder), Path.Combine(contentRoot, _proxyDirectory!));

            return true;
        }
    }

    /// <summary>
    /// Keeps a host's freshly minted certificates, and where they belong, for every host after it.
    /// Does nothing once some host has donated.
    /// </summary>
    public static void Capture(PrinterCertificateAuthority authority, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(authority);

        lock (Gate)
        {
            if (_template is not null)
            {
                return;
            }

            string authorityDirectory = Path.GetDirectoryName(authority.AuthorityKeyPemPath)!;
            string proxyDirectory = Path.GetDirectoryName(authority.LeafKeyPemPath)!;

            string authorityRelative = Path.GetRelativePath(contentRoot, authorityDirectory);
            string proxyRelative = Path.GetRelativePath(contentRoot, proxyDirectory);

            if (LeavesTheContentRoot(authorityRelative) || LeavesTheContentRoot(proxyRelative))
            {
                // Certificates are being written somewhere this cannot follow, so there is nothing
                // safe to donate. Nothing breaks - every host mints its own, as it did before this
                // class existed - and ContentRootIsolationTests is what says so out loud.
                return;
            }

            string staging = Path.Combine(Path.GetTempPath(), $"hs-shared-certs-{Guid.NewGuid():N}");

            CopyInto(authorityDirectory, Path.Combine(staging, AuthorityFolder));
            CopyInto(proxyDirectory, Path.Combine(staging, ProxyFolder));

            // A test host's own content root is deleted with its factory; this outlives all of them, so
            // it is cleaned up on the way out. A killed run leaves one small directory in temp, which
            // is the honest limit of what a process-exit handler can promise.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Discard(staging);

            _authorityDirectory = authorityRelative;
            _proxyDirectory = proxyRelative;
            _template = staging;
        }
    }

    private static bool LeavesTheContentRoot(string relative)
    {
        return Path.IsPathRooted(relative)
               || relative.StartsWith("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies every file in one directory into another, which is what keeps this honest about the
    /// authority's file names: it never states them, so it cannot fall out of step with them.
    /// </summary>
    private static void CopyInto(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void Discard(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Process exit is not the place to fail a run over a temp directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
