using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// The zip an operator unpacks onto a USB stick.
/// </summary>
/// <remarks>
/// Every assertion here corresponds to a way the hand-assembled version failed on 2026-07-28 - a
/// <c>;</c> comment, an omitted key, a PEM renamed <c>.der</c>, files one directory deep. None was a
/// protocol problem; all four were assembly problems, which is what generating the files removes
/// (<c>notes/usb-provisioning-bundle.md</c>).
/// </remarks>
public sealed class ProvisioningBundleBuilderTests : IDisposable
{
    private const string Token = "abcdefghijklmnopqrst";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-bundle-{Guid.NewGuid():N}");

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using MemoryStream stream = new(zip);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using Stream content = entry.Open();
                using MemoryStream buffer = new();
                content.CopyTo(buffer);

                return buffer.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static string IniOf(byte[] zip) => Encoding.UTF8.GetString(Entries(zip)["prusa_printer_settings.ini"]);

    private PrinterCertificateAuthority NewAuthority() =>
        new(Options.Create(new CertificateOptions { Directory = "certs" }),
            new HostEnvironmentAccessor(_root),
            TimeProvider.System,
            NullLogger<PrinterCertificateAuthority>.Instance);

    private ProvisioningBundleBuilder NewBuilder(PrinterCertificateAuthority authority,
                                                bool tls = true,
                                                string host = "printers.example.com",
                                                IHostAddressResolver? resolver = null) =>
        new(Options.Create(new PrusaConnectOptions { PrinterHost = host, PrinterPort = 15443, PrinterTls = tls }),
            Options.Create(new CertificateOptions { ContainerNetworks = ["172.16.0.0/12"] }),
            authority,
            resolver ?? new FakeResolver());

    /// <summary>
    /// Answers what a test says it answers, so "resolves inside the container", "resolves on the LAN"
    /// and "does not resolve" are all producible - only one of which is safe to act on.
    /// </summary>
    private sealed class FakeResolver : IHostAddressResolver
    {
        private readonly Dictionary<string, IPAddress[]> _answers;

        public FakeResolver(Dictionary<string, IPAddress[]>? answers = null)
        {
            _answers = answers ?? [];
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(
                _answers.TryGetValue(name, out IPAddress[]? found) ? found : []);
    }

    /// <summary>
    /// Three files, all at the root.
    /// </summary>
    /// <remarks>
    /// <b>The nesting is the point.</b> A wrapping folder - what every "zip this directory" helper
    /// produces - puts both files one level deep on the stick, where the printer finds neither and
    /// says nothing about it.
    /// </remarks>
    [Fact]
    public async Task TheBundleIsThreeFilesAtTheZipRootAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        byte[] zip = await NewBuilder(authority).BuildAsync("printers.example.com", Token, "Bench printer", CancellationToken.None);

        // Assert
        Entries(zip).Keys.Should().BeEquivalentTo(["prusa_printer_settings.ini", "connect.der", "README.Bundle.md"]);
        Entries(zip).Keys.Should().AllSatisfy(name => name.Should().NotContain("/",
            "a wrapping folder puts every file one level deep on the stick, where the printer finds none of them"));
    }

    /// <summary>
    /// <c>connect.der</c> is the authority itself, byte for byte, carrying no private key.
    /// </summary>
    /// <remarks>
    /// The encoding is not cosmetic: PEM is unsupported by the firmware, and a PEM renamed <c>.der</c>
    /// fails as <c>Error::Tls</c> with no explanation - the documented mistake this path exists to make
    /// unreachable, since nobody chooses an encoding any more.
    /// </remarks>
    [Fact]
    public async Task TheAnchorIsTheAuthorityInDerWithNoPrivateKeyAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        byte[] der = Entries(await NewBuilder(authority).BuildAsync("printers.example.com", Token, "Bench printer", CancellationToken.None))["connect.der"];

        // Assert
        der.Should().BeEquivalentTo(await File.ReadAllBytesAsync(authority.AuthorityDerPath, CancellationToken.None));
        der.Should().StartWith([(byte)0x30], "DER-encoded certificates begin with a SEQUENCE tag");

        using X509Certificate2 shipped = X509CertificateLoader.LoadCertificate(der);
        shipped.Thumbprint.Should().Be(ca.Thumbprint);
        shipped.HasPrivateKey.Should().BeFalse("this file goes to every printer");
    }

    /// <summary>
    /// The ini parses under Buddy's rules: <c>#</c> comments only, and all five keys present.
    /// </summary>
    /// <remarks>
    /// Both halves cost an afternoon. <c>;</c> is not a comment character to this parser
    /// (<c>INI_START_COMMENT_PREFIXES "#"</c> with <c>INI_ALLOW_NO_VALUE 0</c>), so one such line fails
    /// the whole file; and an omitted key is reset to its struct default rather than left alone, which
    /// for <c>token</c> means de-enrolling the printer.
    /// </remarks>
    [Fact]
    public async Task TheIniUsesHashCommentsAndCarriesEveryKeyAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        string ini = IniOf(await NewBuilder(authority).BuildAsync("printers.example.com", Token, "Bench printer", CancellationToken.None));

        // Assert
        ini.Should().NotContain(";", "a ';' line is a parse error here, not a comment");
        ini.Should().Contain("[service::connect]");

        foreach (string key in new[] { "hostname", "port", "tls", "custom_cert", "token" })
        {
            ini.Should().MatchRegex($@"(?m)^{key} = .+$", $"an omitted {key} is reset to its default, not left alone");
        }

        ini.Should().Contain("hostname = printers.example.com")
           .And.Contain("port = 15443")
           .And.Contain("tls = True")
           .And.Contain("custom_cert = 1")
           .And.Contain($"token = {Token}");
    }

    /// <summary>
    /// The file starts with a comment character, not a byte-order mark.
    /// </summary>
    /// <remarks>
    /// A UTF-8 BOM is three bytes of rubbish in front of the first line as far as an ini parser is
    /// concerned - the same silent, unexplained parse failure the <c>;</c> case produces, arriving from
    /// a direction nobody would think to look at.
    /// </remarks>
    [Fact]
    public async Task TheIniHasNoByteOrderMarkAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        byte[] ini = Entries(await NewBuilder(authority).BuildAsync("printers.example.com", Token, "Bench printer", CancellationToken.None))["prusa_printer_settings.ini"];

        // Assert
        ini.Should().StartWith([(byte)'#']);
        ini.Take(3).Should().NotBeEquivalentTo([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
    }

    /// <summary>
    /// The warning that has to travel with the file is in the file.
    /// </summary>
    /// <remarks>
    /// <c>custom_cert</c> is exclusive: it replaces the printer's trust store rather than adding to it,
    /// so a provisioned printer can no longer reach Prusa Connect. That is read at the printer, weeks
    /// later, by someone who never saw the web page it was downloaded from.
    /// </remarks>
    [Fact]
    public async Task TheIniCarriesTheExclusiveTrustStoreWarningAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        string ini = IniOf(await NewBuilder(authority).BuildAsync("printers.example.com", Token, "Bench printer", CancellationToken.None));

        // Assert
        ini.Should().Contain("ENTIRE trust store")
           .And.Contain("cannot talk to Prusa Connect");
    }

    /// <summary>
    /// A name the certificate does not cover is refused rather than written.
    /// </summary>
    /// <remarks>
    /// <b>This is the check that replaced the design note's two network validations.</b> Those asked
    /// whether an operator-supplied anchor was ECDSA and whether it verified the endpoint; both became
    /// true by construction once the server mints its own. What survives is the question that is still
    /// answerable wrongly - whether the address about to be written is one a printer can verify - and
    /// it needs no TLS connection to answer, only the SAN.
    /// </remarks>
    [Fact]
    public async Task ANameTheCertificateDoesNotCoverIsRefusedAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        ProvisioningBundleBuilder builder = NewBuilder(authority);

        // Act
        Func<Task> act = async () => await builder.BuildAsync("192.168.1.50", Token, "Bench printer", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*not an address a printer could use*");
    }

    /// <summary>
    /// The configured address is offered first, so the default needs no thought.
    /// </summary>
    [Fact]
    public async Task TheConfiguredAddressIsOfferedFirstAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["homespool.lan", "192.168.13.238", "printers.example.com"]);

        // Act
        IReadOnlyList<PrinterAddressSuggestion> names =
            await NewBuilder(authority).AvailableNamesAsync(CancellationToken.None);

        // Assert
        names[0].Value.Should().Be("printers.example.com");
        names.Select(suggestion => suggestion.Value).Should()
            .Contain("homespool.lan").And.Contain("192.168.13.238");

        // Each carries what it costs, which is the difference between surviving a moved lease and
        // breaking silently one day - the only thing distinguishing these options to a reader.
        names.Should().AllSatisfy(suggestion => suggestion.Note.Should().NotBeNullOrWhiteSpace());
        names.Single(suggestion => suggestion.Value == "192.168.13.238").Durability
            .Should().Be(AddressDurability.UntilTheLeaseMoves);
    }

    /// <summary>
    /// With TLS off the bundle is one file, and it says so.
    /// </summary>
    /// <remarks>
    /// Shipping a <c>connect.der</c> the printer will never open would leave the operator wondering
    /// what it was for, and <c>custom_cert = 1</c> against a plaintext listener is simply wrong.
    /// </remarks>
    [Fact]
    public async Task WithoutTlsThereIsNoAnchorAndTheIniSaysSoAsync()
    {
        // Arrange - no leaf at all, which is what a plaintext deployment has.
        PrinterCertificateAuthority authority = NewAuthority();

        // Act
        byte[] zip = await NewBuilder(authority, tls: false).BuildAsync("192.168.13.238", Token, "Bench printer", CancellationToken.None);

        // Assert
        Entries(zip).Keys.Should().BeEquivalentTo(["prusa_printer_settings.ini", "README.Bundle.md"],
            "the instructions still ship - and say why there is no certificate");

        string ini = IniOf(zip);
        ini.Should().Contain("tls = False")
           .And.Contain("custom_cert = 0")
           .And.Contain("crosses the network in clear");
    }

    /// <summary>
    /// With no certificate issued, no name is offered - so the page cannot present a choice that
    /// would produce a printer unable to connect.
    /// </summary>
    [Fact]
    public async Task WithNoCertificateThereAreNoNamesToOfferAsync()
    {
        (await NewBuilder(NewAuthority()).AvailableNamesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
