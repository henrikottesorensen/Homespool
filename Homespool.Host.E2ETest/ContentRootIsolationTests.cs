using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.PrintFiles;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Guards the one redirection that keeps a test run out of the repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>No known defect motivated this; three prior ones did.</b> The SQLite database, then uploaded
/// gcode, then the printer certificate each escaped into <c>Homespool.Host/data</c> because a relative
/// path in options is resolved against the content root, and under
/// <c>WebApplicationFactory</c> that is the real project directory. Each was found separately, by its
/// own symptom - accumulating state, 21 stale directories, and a test that read back the developer's
/// own certificate and asserted against it.
/// </para>
/// <para>
/// <see cref="HomespoolFactory"/> now moves the content root itself rather than each component's
/// options, so the next component to keep a file is isolated without anyone thinking about it. This
/// test is what makes that guarantee hold: delete the override and the failure is here, immediately,
/// rather than in the working tree of whoever runs the suite next.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class ContentRootIsolationTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-contentroot-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The content root a test application resolves paths against is a temporary directory, not the
    /// project it was built from.
    /// </summary>
    [Fact]
    public void TheContentRootIsATemporaryDirectory()
    {
        // Act
        string contentRoot = _factory.Services.GetRequiredService<IHostEnvironmentAccessor>().ContentRootPath;

        // Assert
        Path.IsPathRooted(contentRoot).Should().BeTrue();

        contentRoot.Should().StartWith(Path.GetTempPath(),
            "a relative directory in options resolves against this, so anything but a temp path writes "
            + "into the repository - which is how uploads and certificates escaped before");

        Directory.Exists(contentRoot).Should().BeTrue("components expect to be able to write here immediately");
    }

    /// <summary>
    /// And the components that actually keep files land inside it — the property the redirection is
    /// for, asserted through the components rather than through the setting.
    /// </summary>
    /// <remarks>
    /// Both hold a <i>relative</i> directory by default, so this fails the moment either stops
    /// resolving through <see cref="IHostEnvironmentAccessor"/> — the one bypass no override can
    /// catch, since a component injecting <c>IWebHostEnvironment</c> directly would silently get the
    /// project directory back.
    /// </remarks>
    [Fact]
    public void EverythingThatKeepsFilesResolvesInsideIt()
    {
        // Arrange
        string contentRoot = _factory.Services.GetRequiredService<IHostEnvironmentAccessor>().ContentRootPath;

        // Act
        PrinterCertificateAuthority authority = _factory.Services.GetRequiredService<PrinterCertificateAuthority>();
        PrintFileStorageOptions files = _factory.Services.GetRequiredService<IOptions<PrintFileStorageOptions>>().Value;
        CertificateOptions certificates = _factory.Services.GetRequiredService<IOptions<CertificateOptions>>().Value;

        // Assert
        authority.AuthorityDerPath.Should().StartWith(contentRoot);
        authority.LeafPath.Should().StartWith(contentRoot);

        Path.IsPathRooted(files.Directory).Should().BeFalse(
            "the point of this test is that a relative default is safe, so a test that quietly configured "
            + "an absolute one would be asserting nothing");
        Path.IsPathRooted(certificates.Directory).Should().BeFalse("likewise");
    }
}
