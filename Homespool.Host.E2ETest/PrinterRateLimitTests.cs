using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The two halves of the printer limiter, driven over the printer listener: a window per printer,
/// under a ceiling on each route's total. Its own fixture, so the windows it spends are nobody
/// else's.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here enrols a printer, so an admitted request answers 401 rather than 200. That is the
/// property being used: the limiter runs before authentication, so a request that will be refused
/// anyway still spends a permit, and the count is of requests rather than of printers.
/// </para>
/// <para>
/// Every loop counts off the constants rather than off a literal, so a test cannot say the limit is
/// something the application does not.
/// </para>
/// </remarks>
public sealed class PrinterRateLimitTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printer-limit");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// One printer spending its whole window on the HTTP transport leaves another printer's window
    /// untouched - the property a single global window cannot have, and the reason this is
    /// partitioned at all.
    /// </summary>
    [Fact]
    public async Task APrinterSpendingItsOwnWindowDoesNotSpendAnother()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        PrinterIdentity loud = PrinterIdentity.CreateRandom();
        PrinterIdentity quiet = PrinterIdentity.CreateRandom();
        List<HttpStatusCode> answers = [];

        // Act - one printer posts telemetry once past its own limit, then the other posts once.
        for (int i = 0; i <= PrinterRateLimits.HttpTransportPerPrinterLimit; i += 1)
        {
            answers.Add(await PostTelemetryAsync(printers, loud.HeaderFingerprint));
        }

        HttpStatusCode neighbour = await PostTelemetryAsync(printers, quiet.HeaderFingerprint);

        // Assert
        answers[..PrinterRateLimits.HttpTransportPerPrinterLimit]
            .Should().AllSatisfy(status => status.Should().Be(HttpStatusCode.Unauthorized,
                                                              "the window admits its permits, and an admitted request reaches authentication"));

        answers[PrinterRateLimits.HttpTransportPerPrinterLimit]
            .Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");

        neighbour.Should().Be(HttpStatusCode.Unauthorized, "the window that was spent belongs to the printer that spent it");
    }

    /// <summary>
    /// A caller minting a fresh fingerprint per request never meets its own window - and meets the
    /// ceiling instead, which is what keeps the aggregate finite.
    /// </summary>
    /// <remarks>
    /// Driven against the socket policy because its ceiling is the smallest: the two policies share
    /// one code path, and the cost of proving this on the transport's ceiling is ten times the
    /// requests for the same assertion.
    /// </remarks>
    [Fact]
    public async Task TheCeilingBoundsACallerRotatingFingerprints()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        List<HttpStatusCode> answers = [];

        // Act
        for (int i = 0; i <= PrinterRateLimits.SocketCeiling; i += 1)
        {
            using HttpRequestMessage upgrade = new(HttpMethod.Get, "/p/ws");
            upgrade.Headers.TryAddWithoutValidation(Headers.Fingerprint, PrinterIdentity.CreateRandom().HeaderFingerprint);

            using HttpResponseMessage response = await printers.SendAsync(upgrade, TestContext.Current.CancellationToken);
            answers.Add(response.StatusCode);
        }

        // Assert
        answers[..PrinterRateLimits.SocketCeiling]
            .Should().AllSatisfy(status => status.Should().NotBe(HttpStatusCode.TooManyRequests,
                                                                 "a fingerprint nobody has used yet has its whole window"));

        answers[PrinterRateLimits.SocketCeiling]
            .Should().Be(HttpStatusCode.TooManyRequests, "the ceiling counts every fingerprint together");
    }

    /// <summary>
    /// A flood of code requests no longer starves the poll that follows one: the two registration
    /// verbs have separate windows, which is the only isolation that route can have - the printer
    /// names itself in the body of one and not at all in the other.
    /// </summary>
    [Fact]
    public async Task SpendingTheRegistrationWindowDoesNotStopAPrinterPolling()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        List<HttpStatusCode> answers = [];

        // Act - ask for a code once past the ceiling, then poll for one.
        for (int i = 0; i <= PrinterRateLimits.RegistrationStartCeiling; i += 1)
        {
            using HttpResponseMessage response = await printers.PostAsJsonAsync("/p/register", new
            {
                sn = identity.SerialNumber,
                fingerprint = identity.Fingerprint,
                printer_type = identity.PrinterType,
                firmware = identity.Firmware,
            }, TestContext.Current.CancellationToken);

            answers.Add(response.StatusCode);
        }

        using HttpRequestMessage poll = new(HttpMethod.Get, "/p/register");
        poll.Headers.TryAddWithoutValidation(Headers.Code, "NOTACODE00");

        using HttpResponseMessage polled = await printers.SendAsync(poll, TestContext.Current.CancellationToken);

        // Assert
        answers[..PrinterRateLimits.RegistrationStartCeiling]
            .Should().AllSatisfy(status => status.Should().Be(HttpStatusCode.OK, "the window admits its permits"));

        answers[PrinterRateLimits.RegistrationStartCeiling]
            .Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");

        polled.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "polling has a window of its own");
    }

    private static async Task<HttpStatusCode> PostTelemetryAsync(HttpClient printers, string fingerprint)
    {
        using HttpRequestMessage telemetry = new(HttpMethod.Post, "/p/telemetry");
        telemetry.Headers.TryAddWithoutValidation(Headers.Fingerprint, fingerprint);

        using HttpResponseMessage response = await printers.SendAsync(telemetry, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }
}
