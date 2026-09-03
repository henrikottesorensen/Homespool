using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The camera frame endpoint through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint is the whole of the access control for a camera picture.</b> The stream server it
/// fronts has no authorisation of its own - one credential for its entire API, with no notion of
/// which cameras a caller may see - and its own frame URL takes a stream name as a query parameter.
/// So if this route ever answered without checking, knowing a camera's identifier would be enough to
/// watch it. That is why the refusal cases are here rather than left to a unit test of the gate.
/// </para>
/// <para>
/// Nothing here needs a real camera: the cases are about who may ask and what "no picture" looks
/// like, and a camera whose source points nowhere answers exactly as one that is switched off.
/// </para>
/// </remarks>
public sealed class CameraFrameEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-camera-{Guid.NewGuid():N}.db");
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
    /// A camera that has produced nothing yet answers 204, not a 200 with an empty body and not a
    /// stale picture. The caller shows that it is capturing.
    /// </summary>
    [Fact]
    public async Task ACameraWithNoPictureYetAnswersNoContent()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "watcher@example.com");

        Guid uuid = await AddCameraAsync(user, reachable: false);

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/cameras/{uuid}/frame", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
                                        "a camera that has not produced a frame has nothing current to serve, and a stale one is "
                                        + "the failure this design exists to prevent");

        client.Dispose();
    }

    /// <summary>
    /// Another account's camera is indistinguishable from one that does not exist - following
    /// <c>PrinterAccessService</c>, because a UUID that answers differently is a way to find out
    /// which cameras exist.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsCameraIsNotFound()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owner@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "stranger@example.com");

        Guid uuid = await AddCameraAsync(owner, reachable: false);

        using HttpResponseMessage mine = await ownerClient.GetAsync(
            $"/api/v1/cameras/{uuid}/frame", TestContext.Current.CancellationToken);
        using HttpResponseMessage theirs = await stranger.GetAsync(
            $"/api/v1/cameras/{uuid}/frame", TestContext.Current.CancellationToken);

        mine.StatusCode.Should().Be(HttpStatusCode.NoContent, "the owner may ask");
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound,
                                      "a stranger is told the same thing they would be told about a camera that does not exist");

        ownerClient.Dispose();
        stranger.Dispose();
    }

    /// <summary>
    /// A camera that does not exist answers the same as one that is not yours, from the same route.
    /// </summary>
    [Fact]
    public async Task AnUnknownCameraIsNotFound()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "curious@example.com");

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/cameras/{Guid.NewGuid()}/frame", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        client.Dispose();
    }

    /// <summary>
    /// The route is behind the API policy, so an unauthenticated caller never reaches an action -
    /// and gets a status code rather than a login page, which is what <c>ApiStatusCodeCookieEvents</c>
    /// exists for.
    /// </summary>
    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        using HttpClient anonymous = _factory.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(
            $"/api/v1/cameras/{Guid.NewGuid()}/frame", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull("an /api call is answered, not redirected to a login page");
    }

    /// <summary>
    /// Adds a camera owned by this account's default team.
    /// </summary>
    /// <remarks>
    /// Written straight to the database rather than through <c>CameraService</c>, because that path
    /// registers with the stream server and asks it for a picture - neither of which exists here, and
    /// neither of which these cases are about.
    /// </remarks>
    private async Task<Guid> AddCameraAsync(HSUser user, bool reachable)
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
            Source = reachable ? "rtsp://192.0.2.1/live" : "rtsp://192.0.2.99/nowhere",
            TeamId = membership.TeamId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        database.Cameras.Add(camera);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }
}
