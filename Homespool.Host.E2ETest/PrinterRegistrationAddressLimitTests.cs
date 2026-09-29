using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The registration verbs' window per address, driven over the printer listener through the
/// trusted proxy.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this proves that the unit tests cannot is the chain.</b> Every request arrives from the
/// proxy's own address and names its client in <c>X-Real-IP</c>, so the partition only sees two
/// clients if the forwarded-headers middleware ran on the printer listener, believed the proxy, and
/// ran before the limiter. Any of those missing and both callers share the proxy's window.
/// </para>
/// <para>
/// Its own fixture: the host is configured as one that keeps client addresses, which switches these
/// windows on for every test in the class, and <see cref="PrinterRateLimitTests"/> counts the
/// registration ceiling from a single client that would otherwise meet this limit first.
/// </para>
/// </remarks>
public sealed class PrinterRegistrationAddressLimitTests : IAsyncLifetime
{
    private const string ProxyAddress = "172.28.0.2";
    private const string Loud = "203.0.113.7";
    private const string Quiet = "198.51.100.9";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printer-address-limit");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);
        _root.ConfigurationOverrides["XForwarded:KnownProxies:0"] = ProxyAddress;
        _root.ConfigurationOverrides["XForwarded:ClientAddressesUnreliable"] = "false";

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddTransient<IStartupFilter>(_ => new FromTheProxy(IPAddress.Parse(ProxyAddress)))));

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// One client spending its code requests leaves another client's untouched - the property the
    /// ceiling alone cannot have, where one caller could refuse every printer enrolling anywhere.
    /// </summary>
    [Fact]
    public async Task OneAddressSpendingItsCodeRequestsDoesNotSpendAnother()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        List<HttpStatusCode> answers = [];

        // Act - one client asks for a code once past its window, then another asks once.
        for (int i = 0; i <= PrinterRateLimits.RegistrationStartPerAddressLimit; i += 1)
        {
            answers.Add(await RequestCodeAsync(printers, identity, Loud));
        }

        HttpStatusCode neighbour = await RequestCodeAsync(printers, identity, Quiet);

        // Assert
        answers[..PrinterRateLimits.RegistrationStartPerAddressLimit]
            .Should().AllSatisfy(status => status.Should().Be(HttpStatusCode.OK, "the window admits its permits"));

        answers[PrinterRateLimits.RegistrationStartPerAddressLimit]
            .Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");

        neighbour.Should().Be(HttpStatusCode.OK, "the window that was spent belongs to the address that spent it");
    }

    /// <summary>
    /// The same for the poll, whose window is sized for one SDK printer asking every second.
    /// </summary>
    [Fact]
    public async Task OneAddressSpendingItsPollsDoesNotSpendAnother()
    {
        // Arrange
        using HttpClient printers = PrinterListener.CreateClient(_factory);
        List<HttpStatusCode> answers = [];

        // Act
        for (int i = 0; i <= PrinterRateLimits.RegistrationPollPerAddressLimit; i += 1)
        {
            answers.Add(await PollAsync(printers, Loud));
        }

        HttpStatusCode neighbour = await PollAsync(printers, Quiet);

        // Assert
        answers[..PrinterRateLimits.RegistrationPollPerAddressLimit]
            .Should().AllSatisfy(status => status.Should().NotBe(HttpStatusCode.TooManyRequests, "the window admits its permits"));

        answers[PrinterRateLimits.RegistrationPollPerAddressLimit]
            .Should().Be(HttpStatusCode.TooManyRequests, "the permit after the last is refused");

        neighbour.Should().NotBe(HttpStatusCode.TooManyRequests, "the window that was spent belongs to the address that spent it");
    }

    private static async Task<HttpStatusCode> RequestCodeAsync(HttpClient printers, PrinterIdentity identity, string client)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/p/register")
        {
            Content = JsonContent.Create(new
            {
                sn = identity.SerialNumber,
                fingerprint = identity.Fingerprint,
                printer_type = identity.PrinterType,
                firmware = identity.Firmware,
            }),
        };
        request.Headers.TryAddWithoutValidation("X-Real-IP", client);

        using HttpResponseMessage response = await printers.SendAsync(request, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> PollAsync(HttpClient printers, string client)
    {
        using HttpRequestMessage poll = new(HttpMethod.Get, "/p/register");
        poll.Headers.TryAddWithoutValidation(Headers.Code, "NOTACODE00");
        poll.Headers.TryAddWithoutValidation("X-Real-IP", client);

        using HttpResponseMessage response = await printers.SendAsync(poll, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    /// <summary>
    /// Makes every request arrive from the trusted proxy's address, which TestServer otherwise leaves
    /// absent - the same seam <c>ForwardedHostTests</c> uses.
    /// </summary>
    private sealed class FromTheProxy(IPAddress peer) : IStartupFilter
    {
        public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = peer;

                    await nextMiddleware();
                });

                next(app);
            };
        }
    }
}
