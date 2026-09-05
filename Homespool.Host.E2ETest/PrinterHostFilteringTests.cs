using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Certificates;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A printer presenting a <c>Host</c> the certificate vouches for reaches the application, through
/// the real pipeline with the host filter live.
/// </summary>
/// <remarks>
/// <para>
/// This is the request both appliance printers made every half minute for a morning: <c>GET /p/ws</c>
/// on the printer port, with the machine's bare address as <c>Host</c>, answered 400 by the
/// framework's host filter before anything here ran. The same request under the machine's hostname
/// was answered 401 by the same host at the same minute — the difference being only which names the
/// filter had been told.
/// </para>
/// <para>
/// The test host's own configuration allows every host, which is why nothing else in this suite
/// meets the filter; this one narrows the list to <c>localhost</c> so that it bites, and then shows
/// the leaf widening it again.
/// </para>
/// </remarks>
public sealed class PrinterHostFilteringTests : IAsyncLifetime
{
    private const string BareAddress = "192.0.2.10";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("hostfilter");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        // The floor compose composes, minus the printer host: the case under test is a printer told
        // an address that appears in the certificate and in no configured list.
        _factory.ConfigurationOverrides["AllowedHosts"] = "localhost";

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// A name the leaf does not cover is refused; the moment a leaf covers it, the same request is
    /// answered by the application — and the host was not restarted in between.
    /// </summary>
    /// <remarks>
    /// One test rather than two because the second half is only meaningful against a host that
    /// already answered the first: it proves the filter, the ordering against the framework's own
    /// post-configure, and the change token, on the exact request that failed.
    /// </remarks>
    [Fact]
    public async Task AHostTheLeafVouchesForIsAnsweredWithoutARestart()
    {
        // Arrange
        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpResponseMessage before = await SendAsPrinterAsync(printer, BareAddress);

        _factory.Services.GetRequiredService<PrinterCertificateAuthority>()
                .IssueLeaf([HomespoolFactory.PrinterHost, BareAddress])
                .Dispose();

        using HttpResponseMessage after = await SendAsPrinterAsync(printer, BareAddress);

        // Assert
        before.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                                      "nothing vouches for the address yet, so the framework's host filter refuses it");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                                     "the leaf now covers the address, so the request reaches the printer authentication that "
                                     + "answers a tokenless connection - which is what the hostname got at the same minute");
    }

    /// <summary>
    /// The configured printer host is allowed even though the override above left it out — it is what
    /// every provisioning bundle names, and it must never depend on somebody also typing it into the
    /// people-facing list.
    /// </summary>
    [Fact]
    public async Task TheConfiguredPrinterHostIsAllowedWithoutBeingListed()
    {
        // Arrange
        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpResponseMessage response = await SendAsPrinterAsync(printer, HomespoolFactory.PrinterHost);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A host neither configured nor on the certificate stays refused: the leaf widens the list to
    /// exactly what it vouches for, not to everything.
    /// </summary>
    [Fact]
    public async Task AHostNobodyVouchesForStaysRefused()
    {
        // Arrange
        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpResponseMessage response = await SendAsPrinterAsync(printer, "203.0.113.5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// <c>GET /p/ws</c> with the given <c>Host</c>, carrying the printer port so the request still
    /// lands on the printer listener — the filter ignores the port, the listener simulation reads it.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsPrinterAsync(HttpClient printer, string host)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/p/ws");
        request.Headers.Host = $"{host}:{PrinterListener.PortOf(_factory)}";

        return await printer.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
