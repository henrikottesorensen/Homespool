using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That the storage-listing route matches the URLs it is supposed to - in particular that the root
/// listing is reachable, which depends on a catch-all segment matching nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Routing is asserted through the status code rather than by inspecting the route table:
/// authorisation runs <i>after</i> routing, so a matched route answers 401 to an anonymous caller
/// while an unmatched one answers 404. That distinction needs no printer, no database rows and no
/// connection, which is what makes it worth having before the listing itself can be exercised
/// end to end.
/// </para>
/// <para>
/// The verb is asserted the same way: the listing is a POST, and a GET to it is a 405.
/// </para>
/// <para>
/// The behaviour of the endpoint - what it does once a printer is on the other end - is the fake's
/// job and is not tested here.
/// </para>
/// </remarks>
public sealed class PrinterStorageRouteTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("storageroute");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        // Routing as the published application does it. From build output, which is what a test host
        // runs, MapStaticAssets adds a GET-and-HEAD fallback on {**path:file}, and the matcher counts
        // that catch-all as a candidate for every path before it tries the constraint - so a GET to a
        // route with no GET answers 404, and any other verb to a path with no route answers 405. A
        // published application has no such fallback, and answers the other way round.
        _factory.ConfigurationOverrides["ReloadStaticAssetsAtRuntime"] = "false";

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

    [Theory]

    // The root listing, which is the whole reason the catch-all has to match an empty value.
    [InlineData("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/usb")]
    [InlineData("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/usb/")]

    // A nested directory, and one whose name needs escaping - a captured listing really does contain
    // a filename with a literal %20 in it, which has to arrive as %2520.
    [InlineData("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/usb/sub/dir")]
    [InlineData("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/usb/wavy%2520vase.bgcode")]
    public async Task TheStorageRouteMatches(string url)
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                                        "a matched route challenges an anonymous caller, where an unmatched one would be 404");
    }

    /// <summary>
    /// <c>usb</c> is a literal segment because it is the only storage firmware has - so a wrong root
    /// is refused by routing, without a command being spent on earning "Forbidden path".
    /// </summary>
    [Fact]
    public async Task AStorageRootThatIsNotUsbDoesNotRoute()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response =
            await client.PostAsync("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/sdcard",
                                   null,
                                   TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A listing is obtained by sending the printer a command, so the route answers no GET: a GET is
    /// what a link, a prefetch or an image tag on another origin can make a signed-in browser send.
    /// </summary>
    [Fact]
    public async Task TheStorageRouteAnswersNoGet()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response =
            await client.GetAsync("/api/v1/printers/11111111-1111-1111-1111-111111111111/storage/usb",
                                  TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
