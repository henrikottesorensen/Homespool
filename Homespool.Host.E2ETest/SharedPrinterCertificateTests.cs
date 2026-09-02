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
/// The first host in a run legitimately mints and donates; the second must not, so this drives two
/// and watches the second. <c>Directory</c> is the mint warning's own property, read structurally
/// rather than by message text like every other assertion here.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class SharedPrinterCertificateTests
{
    [Fact]
    public async Task ASecondHostIsHandedTheAuthorityInsteadOfMintingOne()
    {
        // Arrange - the donor, which may or may not be the run's first host and may therefore mint.
        string donorDatabase = Path.Combine(Path.GetTempPath(), $"hs-shared-donor-{Guid.NewGuid():N}.db");
        string heirDatabase = Path.Combine(Path.GetTempPath(), $"hs-shared-heir-{Guid.NewGuid():N}.db");

        try
        {
            string donorThumbprint;

            await using (HomespoolFactory donor = new($"Data Source={donorDatabase}"))
            {
                _ = donor.Server;

                using X509Certificate2 authority = donor.Services
                                                        .GetRequiredService<PrinterCertificateAuthority>()
                                                        .LoadAuthorityCertificate()!;

                donorThumbprint = authority.Thumbprint;
            }

            // Act - a host started after some host has donated.
            CapturingSink logs = new();

            await using HomespoolFactory heir = new($"Data Source={heirDatabase}", extraSinks: [logs]);

            _ = heir.Server;

            // Assert
            logs.FindPropertyValue("Directory").Should()
                .BeNull("a host that was handed an authority must not mint one, and minting is the "
                        + "only thing that logs a Directory here");

            using X509Certificate2 inherited = heir.Services
                                                   .GetRequiredService<PrinterCertificateAuthority>()
                                                   .LoadAuthorityCertificate()!;

            inherited.Thumbprint.Should().Be(donorThumbprint, "it is the donated authority, not a fresh one");
        }
        finally
        {
            foreach (string database in new[] { donorDatabase, heirDatabase })
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
