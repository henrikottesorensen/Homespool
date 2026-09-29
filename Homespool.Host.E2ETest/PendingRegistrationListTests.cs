using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The claim page's list of printers waiting to be added, from a printer's own <c>POST /p/register</c>
/// to the page a person reads.
/// </summary>
/// <remarks>
/// The page model is tested on its own; this is the one test that the two new columns are written by
/// the real registration and that the markup shows them.
/// </remarks>
public sealed class PendingRegistrationListTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("pendinglist");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task APrinterThatRegisteredIsListedOnTheClaimPage()
    {
        using (HttpClient printer = PrinterListener.CreateClient(_factory))
        {
            using HttpResponseMessage registered = await EnrolmentFlowHelper.SendPrinterRegisterAsync(printer, new
            {
                sn = "TEST-0001",
                fingerprint = "TESTFINGERPRINT0000000000000000000000000000000000",
                printer_type = "7.1.0",
                firmware = "6.5.7+stranger-text",
            });

            registered.EnsureSuccessStatusCode();
        }

        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "lister@example.com");

        using (client)
        {
            string page = await client.GetStringAsync("/Printers/Claim", TestContext.Current.CancellationToken);

            page.Should().Contain("COREONE", "the model is named from the firmware's table");
            page.Should().Contain("6.5.7");
            page.Should().NotContain("stranger-text", "only the version the firmware string parsed to is shown");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }
}
