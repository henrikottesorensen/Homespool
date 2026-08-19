using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using AwesomeAssertions;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That every script and stylesheet a page needs is served by this deployment.
/// </summary>
/// <remarks>
/// <para>
/// This is a self-hosted appliance whose job is to run on a LAN with printers on it, so a
/// deployment with no route to the internet is an ordinary one rather than a degraded one. The
/// scaffold's arrangement - CDN first, vendored copy as fallback - inverts that assumption on every
/// production page load.
/// </para>
/// <para>
/// <b>Asserted against the markup rather than a rendered page, and that is the second attempt.</b>
/// The CDN references lived in the <c>environment exclude="Development"</c> half of <c>_Layout</c>,
/// so rendering a page under the test host exercises the <i>other</i> branch: a first version of
/// this test asked a <c>Production</c>-configured factory for the login page and passed happily
/// with a CDN link put back to check it. Minimal hosting fixes the environment when
/// <c>WebApplication.CreateBuilder</c> runs, before <c>WithWebHostBuilder</c> can move it. Reading
/// the source sidesteps the environment and states the actual rule: no view, in any branch of any
/// conditional, names a host.
/// </para>
/// <para>
/// The path comes from <see cref="CallerFilePathAttribute"/> - the compiler's record of where this
/// file was built from - because the test host's content root is a temporary directory by design.
/// </para>
/// </remarks>
public sealed class LocalAssetTests
{
    [Fact]
    public void NoViewReferencesAThirdPartyHost()
    {
        // Arrange
        string views = Path.Combine(RepositoryRoot(), "Homespool.Host", "Pages");

        Directory.Exists(views).Should().BeTrue("the test needs to find the views to be worth anything");

        // Act
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                // Resources only. A hyperlink to somewhere else on the internet is a link a person
                // chooses to follow - the footer's link to the project's own repository is one - and
                // costs the page nothing unless clicked. What must not appear is markup that makes
                // the browser fetch from a third party to render this page at all.
                bool fetchesScript = line.Contains("src=\"http", StringComparison.OrdinalIgnoreCase);
                bool fetchesStylesheet = line.Contains("<link", StringComparison.OrdinalIgnoreCase)
                                         && line.Contains("href=\"http", StringComparison.OrdinalIgnoreCase);

                if (fetchesScript || fetchesStylesheet)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        // Assert
        offenders.Should().BeEmpty(
            "this is a self-hosted appliance that has to work on a LAN with no route to the internet, "
            + "so every script and stylesheet is served from the deployment itself");
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // <root>/Homespool.Host.E2ETest/LocalAssetTests.cs
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
    }
}
