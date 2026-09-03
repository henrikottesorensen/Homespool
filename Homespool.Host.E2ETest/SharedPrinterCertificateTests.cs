using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Certificates;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Guards the one thing <see cref="SharedPrinterCertificates"/> exists to do: keep a test host from
/// minting a printer authority of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a performance invariant that no ordinary test can notice.</b> A host that mints is
/// correct in every respect - the same files end up in the same places, and every other test passes -
/// it just spends about 1.2 seconds on key derivation doing it, once per test. That was 39% of this
/// suite's run before the sharing existed, and it is exactly the kind of cost that comes back
/// silently.
/// </para>
/// <para>
/// <b>The specific regression this catches is planting too late.</b> Copying the certificates in
/// after <c>base.CreateHost</c> returns is the natural-looking place to do it and saves nothing:
/// <c>Program</c> mints on the startup path, so by then the host has already paid. It cannot be
/// caught by comparing certificates between hosts, because a late copy leaves the right files on
/// disk either way - which is why this asserts on the mint itself.
/// </para>
/// <para>
/// The first host in a run legitimately mints and donates, so this drives one to guarantee a donation
/// has happened and then watches two started afterwards. <c>Directory</c> is the mint warning's own
/// property, read structurally rather than by message text like every other assertion here.
/// </para>
/// <para>
/// <b>It compares the two heirs with each other rather than with the first host, and that is not
/// fussiness.</b> The suite runs classes in parallel, so which host wins the race to donate is
/// undecided - another class may capture the template first, leaving the host started here with a
/// perfectly correct authority that nobody inherits. Asserting the heirs match <i>it</i> failed
/// exactly that way. The template never changes once captured, so any two hosts started after a
/// donation must agree with each other however the race went, and that is the invariant worth
/// holding.
/// </para>
/// </remarks>
public sealed class SharedPrinterCertificateTests
{
    [Fact]
    public async Task ASecondHostIsHandedTheAuthorityInsteadOfMintingOne()
    {
        // Arrange - the donor, which may or may not be the run's first host and may therefore mint.
        string donorDatabase = Path.Combine(Path.GetTempPath(), $"hs-shared-donor-{Guid.NewGuid():N}.db");
        string heirDatabase = Path.Combine(Path.GetTempPath(), $"hs-shared-heir-{Guid.NewGuid():N}.db");
        string siblingDatabase = Path.Combine(Path.GetTempPath(), $"hs-shared-sibling-{Guid.NewGuid():N}.db");

        try
        {
            // A host, only to guarantee that some host has donated by the time the two below start.
            // Deliberately not asserted against: whether *this* one is the donor is a race the suite
            // runs in parallel, and another class may have captured the template first - in which case
            // this host holds an authority nobody inherits, which is correct and uninteresting.
            await using (HomespoolFactory first = new($"Data Source={donorDatabase}"))
            {
                _ = first.Server;
            }

            // Act - two hosts started once a template certainly exists. The template never changes
            // after the first capture, so these two must agree with each other however the race went.
            CapturingSink logs = new();

            await using HomespoolFactory heir = new($"Data Source={heirDatabase}", extraSinks: [logs]);
            await using HomespoolFactory sibling = new($"Data Source={siblingDatabase}");

            _ = heir.Server;
            _ = sibling.Server;

            // Assert
            logs.FindPropertyValue("Directory").Should()
                .BeNull("a host that was handed an authority must not mint one, and minting is the "
                        + "only thing that logs a Directory here");

            using X509Certificate2 inherited = heir.Services
                                                   .GetRequiredService<PrinterCertificateAuthority>()
                                                   .LoadAuthorityCertificate()!;
            using X509Certificate2 alsoInherited = sibling.Services
                                                          .GetRequiredService<PrinterCertificateAuthority>()
                                                          .LoadAuthorityCertificate()!;

            inherited.Thumbprint.Should().Be(alsoInherited.Thumbprint,
                                             "both were handed the one donated authority, not one minted each");
        }
        finally
        {
            foreach (string database in new[] { donorDatabase, heirDatabase, siblingDatabase })
            {
                foreach (string path in new[] { database, database + "-wal", database + "-shm" })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }
    }
}
