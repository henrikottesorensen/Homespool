using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The live-view endpoints through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>The signalling route is where a live view is permitted or refused, and there is nowhere else it
/// could be.</b> WebRTC media goes straight from the stream server to the browser over a published
/// port that Homespool is not in the path of — so this exchange, which is what hands over the ICE
/// credentials, is the only moment the camera's permission can be applied. If it ever answered
/// without checking, the published port would stop being harmless.
/// </para>
/// <para>
/// No stream server runs here, so nothing negotiates. That is the right shape for these cases all
/// the same: every one of them is about who may ask and what an unavailable camera answers, and both
/// are settled before the sidecar would be reached.
/// </para>
/// </remarks>
public sealed class CameraLiveEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-camlive-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

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
    /// With no stream server and no address configured, live view is not on offer — which is the
    /// answer that keeps the button off the page rather than putting one there that cannot work.
    /// </summary>
    [Fact]
    public async Task ACameraThatCannotBeWatchedSaysSo()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "watcher@example.com");

        Guid uuid = await AddCameraAsync(user);

        CameraLiveOption? option = await client.GetFromJsonAsync<CameraLiveOption>(
            $"/api/v1/cameras/{uuid}/live", TestContext.Current.CancellationToken);

        option.Should().NotBeNull();
        option!.Available.Should().BeFalse();

        client.Dispose();
    }

    /// <summary>
    /// The same rule as the frame endpoint, and it has to be the same: a UUID that answers
    /// differently is a way to learn which cameras exist.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsCameraIsNotFound()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owner@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "stranger@example.com");

        Guid uuid = await AddCameraAsync(owner);

        using HttpResponseMessage mine = await ownerClient.GetAsync(
            $"/api/v1/cameras/{uuid}/live", TestContext.Current.CancellationToken);
        using HttpResponseMessage theirs = await stranger.GetAsync(
            $"/api/v1/cameras/{uuid}/live", TestContext.Current.CancellationToken);

        mine.StatusCode.Should().Be(HttpStatusCode.OK, "the owner may ask");
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ownerClient.Dispose();
        stranger.Dispose();
    }

    /// <summary>
    /// The one that matters most: an offer for somebody else's camera is refused before it reaches
    /// the stream server, so no ICE credentials are ever handed out for it.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsCameraCannotBeNegotiated()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owner2@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "stranger2@example.com");

        Guid uuid = await AddCameraAsync(owner);

        using HttpResponseMessage response = await stranger.PostAsJsonAsync(
            $"/api/v1/cameras/{uuid}/webrtc",
            new WebRtcDescription("offer", "v=0"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ownerClient.Dispose();
        stranger.Dispose();
    }

    /// <summary>
    /// An offer carrying no session description is the caller's mistake, not the gateway's, and is
    /// answered before anything is forwarded.
    /// </summary>
    [Fact]
    public async Task AnEmptyOfferIsRefused()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "empty@example.com");

        Guid uuid = await AddCameraAsync(user);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/cameras/{uuid}/webrtc",
            new WebRtcDescription("offer", "   "),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    /// <summary>
    /// A camera that cannot be watched refuses the negotiation too, rather than trusting the page to
    /// have asked first — this route is reachable on its own.
    /// </summary>
    [Fact]
    public async Task ACameraThatCannotBeWatchedRefusesAnOffer()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "hopeful@example.com");

        Guid uuid = await AddCameraAsync(user);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/cameras/{uuid}/webrtc",
            new WebRtcDescription("offer", "v=0"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        client.Dispose();
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        using HttpClient anonymous = _factory.CreateClient();

        using HttpResponseMessage live = await anonymous.GetAsync(
            $"/api/v1/cameras/{Guid.NewGuid()}/live", TestContext.Current.CancellationToken);
        using HttpResponseMessage offer = await anonymous.PostAsJsonAsync(
            $"/api/v1/cameras/{Guid.NewGuid()}/webrtc",
            new WebRtcDescription("offer", "v=0"),
            TestContext.Current.CancellationToken);

        live.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        offer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        offer.Headers.Location.Should().BeNull("an /api call is answered, not redirected to a login page");
    }

    /// <summary>
    /// Written straight to the database rather than through <c>CameraService</c>, which would try to
    /// register with a stream server that is not running here.
    /// </summary>
    private async Task<Guid> AddCameraAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await database.TeamMembers
                                              .FirstAsync(member => member.UserId == user.Id,
                                                          TestContext.Current.CancellationToken);

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Test camera",
            Source = "rtsp://192.0.2.1/live",
            TeamId = membership.TeamId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        database.Cameras.Add(camera);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }
}
