using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>GET /api/v1/user</c> answers every credential, gives the address only to one allowed to see it,
/// and reports each team's capabilities as the credential may use them.
/// </summary>
/// <remarks>
/// The withheld case is asserted on the raw body, where a leak would show, rather than on the one
/// field a parser happens to read.
/// </remarks>
public sealed class UserEndpointTests : IAsyncLifetime
{
    private const string Address = "user-endpoint@example.com";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("userapi");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

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
    /// A token without <c>ViewAccountDetails</c> still gets a 200, since scripts call this to check a
    /// token works, but the address is nowhere in the body.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutViewAccountDetailsIsAnsweredWithoutTheAddress()
    {
        // Arrange
        using HttpClient client = await TokenClientAsync(Capability.UploadOwnFiles, Capability.Print);

        // Act
        (HttpStatusCode status, string raw) = await GetUserAsync(client);

        // Assert
        status.Should().Be(HttpStatusCode.OK, "any valid token may ask who it belongs to");
        raw.Should().NotContain(Address);

        using JsonDocument user = JsonDocument.Parse(raw);
        user.RootElement.GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>A token whose scope includes it is told the address.</summary>
    [Fact]
    public async Task ATokenWithViewAccountDetailsIsToldTheAddress()
    {
        // Arrange
        using HttpClient client = await TokenClientAsync(Capability.ViewAccountDetails);

        // Act
        (HttpStatusCode status, string raw) = await GetUserAsync(client);

        // Assert
        status.Should().Be(HttpStatusCode.OK);

        using JsonDocument user = JsonDocument.Parse(raw);
        user.RootElement.GetProperty("email").GetString().Should().Be(Address);
    }

    /// <summary>
    /// A team's capabilities are what the token may use there, not what its owner holds. The owner
    /// created the team and holds every printer and camera capability on it.
    /// </summary>
    [Fact]
    public async Task ATeamsCapabilitiesAreNarrowedByTheTokensScope()
    {
        // Arrange
        using HttpClient client = await TokenClientAsync(Capability.UploadOwnFiles, Capability.Print);

        // Act
        (_, string raw) = await GetUserAsync(client);

        // Assert
        using JsonDocument user = JsonDocument.Parse(raw);
        string?[] capabilities = [.. user.RootElement.GetProperty("teams")[0].GetProperty("capabilities")
                                         .EnumerateArray().Select(capability => capability.GetString())];

        capabilities.Should().BeEquivalentTo(
            [nameof(Capability.ViewPrinter), nameof(Capability.Print)],
            "Print implies ViewPrinter, and UploadOwnFiles is no membership's to grant");
    }

    /// <summary>
    /// A signed-in browser session is not narrowed, so it sees the address and the whole membership.
    /// </summary>
    [Fact]
    public async Task ASignedInSessionIsToldTheAddressAndTheWholeMembership()
    {
        // Arrange
        (_, HttpClient signedIn) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, Address);
        using HttpClient client = signedIn;

        // Act
        (HttpStatusCode status, string raw) = await GetUserAsync(client);

        // Assert
        status.Should().Be(HttpStatusCode.OK);

        using JsonDocument user = JsonDocument.Parse(raw);
        user.RootElement.GetProperty("email").GetString().Should().Be(Address);
        user.RootElement.GetProperty("teams")[0].GetProperty("capabilities").EnumerateArray()
            .Select(capability => capability.GetString())
            .Should().Contain([nameof(Capability.ManagePrinter), nameof(Capability.ManageCamera)],
                              "the team's creator holds these, and a session narrows nothing");
    }

    private static async Task<(HttpStatusCode status, string raw)> GetUserAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v1/user", TestContext.Current.CancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<HttpClient> TokenClientAsync(params Capability[] scope)
    {
        (HSUser user, HttpClient signedIn) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, Address);
        signedIn.Dispose();

        using IServiceScope services = _factory.Services.CreateScope();
        ApiTokenService tokens = services.ServiceProvider.GetRequiredService<ApiTokenService>();
        (_, string plaintext) = await tokens.CreateAsync(user.Id, "user", CapabilitySet.Parse(CapabilitySet.Format(scope)),
                                                         CancellationToken.None);

        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        return client;
    }
}
