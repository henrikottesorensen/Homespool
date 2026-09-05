using System;
using System.IO;

using AwesomeAssertions;

using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Certificates;
using Homespool.Host.Listeners;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The host filter allows what the printer certificate vouches for — and keeps allowing it after a
/// reissue, without a restart.
/// </summary>
/// <remarks>
/// Both printers on the appliance were answered 400 by the framework's host filter, before any of
/// this application ran, because they had been provisioned for the machine's bare address and the
/// composed list named only its hostname. The certificate covered the address; the filter had never
/// been told. These pin the rule that closes that gap: the leaf's names are the allowed list.
/// </remarks>
public sealed class PrinterHostFilteringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-hostfilter-{Guid.NewGuid():N}");
    private readonly PrinterLeafChangeToken _leafChanged = new();

    private PrinterCertificateAuthority NewAuthority()
    {
        return new(Options.Create(new CertificateOptions { Directory = "certs", AuthorityPassphrase = "unit test passphrase" }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<PrinterCertificateAuthority>.Instance,
                   _leafChanged);
    }

    private PrinterHostFiltering NewFiltering(PrinterCertificateAuthority authority, string printerHost)
    {
        ServiceCollection services = new();
        services.Configure<PrusaConnectOptions>(options => options.PrinterHost = printerHost);

        return new(authority, _leafChanged, services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<PrusaConnectOptions>>());
    }

    /// <summary>
    /// Every name on the leaf joins the configured list, once, and the configured list is kept.
    /// </summary>
    /// <remarks>
    /// The fixture is the appliance's own: the leaf covers the hostname and the bare address, and the
    /// composed list names only the hostname. The framework's own post-configure leaves an
    /// <i>array</i> in the property, which cannot be added to — so the list is written over rather than
    /// appended, and this arranges the array to make sure of it.
    /// </remarks>
    [Fact]
    public void TheLeafsNamesJoinTheConfiguredList()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.IssueLeaf(["homespool.example.net", "192.168.13.108"]).Dispose();

        HostFilteringOptions options = new() { AllowedHosts = new[] { "people.example.com", "homespool.example.net", "localhost" } };

        // Act
        NewFiltering(authority, "homespool.example.net").PostConfigure(Options.DefaultName, options);

        // Assert
        options.AllowedHosts.Should().Equal(["people.example.com", "homespool.example.net", "localhost", "192.168.13.108"],
                                            "the composed list is the floor, and the leaf adds what it vouches for");
    }

    /// <summary>
    /// A leaf reissued for a new name makes that name allowed through the options monitor, which is
    /// the path the middleware reads — with nothing restarted.
    /// </summary>
    /// <remarks>
    /// The post-configure alone would satisfy the test above and still leave a reissue answering 400
    /// until the next restart, because the monitor caches. This composes the monitor the way the
    /// application does and looks at what it hands out after the authority issues.
    /// </remarks>
    [Fact]
    public void AReissueIsSeenByTheOptionsMonitorWithoutARestart()
    {
        // Arrange - the options as the framework leaves them, then the filter registered after it.
        PrinterCertificateAuthority authority = NewAuthority();
        authority.IssueLeaf(["homespool.example.net"]).Dispose();

        ServiceCollection services = new();
        services.AddOptions();
        services.Configure<PrusaConnectOptions>(options => options.PrinterHost = "homespool.example.net");
        services.Configure<HostFilteringOptions>(options => options.AllowedHosts = ["localhost"]);
        services.AddSingleton(authority);
        services.AddSingleton(_leafChanged);
        services.AddSingleton<PrinterHostFiltering>();
        services.AddSingleton<IPostConfigureOptions<HostFilteringOptions>>(
            provider => provider.GetRequiredService<PrinterHostFiltering>());
        services.AddSingleton<IOptionsChangeTokenSource<HostFilteringOptions>>(
            provider => provider.GetRequiredService<PrinterHostFiltering>());

        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<HostFilteringOptions> monitor = provider.GetRequiredService<IOptionsMonitor<HostFilteringOptions>>();

        monitor.CurrentValue.AllowedHosts.Should().NotContain("192.168.13.108", "nothing vouches for it yet");

        // Act
        authority.IssueLeaf(["homespool.example.net", "192.168.13.108"]).Dispose();

        // Assert
        monitor.CurrentValue.AllowedHosts.Should().Contain("192.168.13.108",
                                                          "a reissue that adds a name must not wait for a restart to be honoured");
        monitor.CurrentValue.AllowedHosts.Should().Contain("localhost", "the configured floor survives the rebuild");
    }

    /// <summary>
    /// With no leaf on disk — printer TLS off — the configured host is still allowed.
    /// </summary>
    /// <remarks>
    /// A plaintext printer was told the configured name and nothing else, and there is no
    /// certificate to derive it from. It is added verbatim, not resolved: this runs synchronously on
    /// the request path, and a resolver's timeout would be paid there.
    /// </remarks>
    [Fact]
    public void WithNoLeafTheConfiguredHostIsStillAllowed()
    {
        // Arrange
        HostFilteringOptions options = new() { AllowedHosts = new[] { "localhost" } };

        // Act
        NewFiltering(NewAuthority(), "printers.lan").PostConfigure(Options.DefaultName, options);

        // Assert
        options.AllowedHosts.Should().Equal(["localhost", "printers.lan"]);
    }

    /// <summary>
    /// The token the filter hands out fires when the authority issues, and the next one is fresh.
    /// </summary>
    [Fact]
    public void IssuingALeafFiresTheChangeToken()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        PrinterHostFiltering filtering = NewFiltering(authority, "printers.lan");

        IChangeToken before = filtering.GetChangeToken();
        before.HasChanged.Should().BeFalse();

        // Act
        authority.IssueLeaf(["printers.lan"]).Dispose();

        // Assert
        before.HasChanged.Should().BeTrue("the leaf on disk is not the one the last answer was read from");
        filtering.GetChangeToken().HasChanged.Should().BeFalse("a token taken after the issuance waits for the next one");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
