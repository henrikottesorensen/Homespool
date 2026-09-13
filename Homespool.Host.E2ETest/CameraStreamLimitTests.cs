using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The ceiling on an account's live MJPEG streams, through the real endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>No stream server runs here, and that is what makes the order visible.</b> The codec probe is
/// replaced so every camera answers JPEG and reaches the relay; the relay then has no sidecar to open
/// and answers 502 at once. So a 429 can only be the ceiling refusing before the relay was asked, and
/// a 502 is proof the request got past it.
/// </para>
/// <para>
/// <b>The open streams are taken from the limiter directly</b>, standing in for viewers already
/// watching. Holding five real streams open would need a sidecar producing frames, and what is under
/// test is the endpoint's use of the count rather than the relay.
/// </para>
/// </remarks>
public sealed class CameraStreamLimitTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camlimit");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICameraCodecProbe>(new JpegOnly());
        }));

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

    [Fact]
    public async Task AnAccountAtItsLimitIsRefusedBeforeTheSidecarIsAsked()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "watcher@example.com");

        using (client)
        {
            Guid uuid = await AddCameraAsync(user);
            List<IDisposable?> watching = Hold(user.Id, 5);

            try
            {
                using HttpResponseMessage refused = await client.GetAsync(
                    $"/api/v1/cameras/{uuid}/stream.mjpeg", TestContext.Current.CancellationToken);

                refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
                refused.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

                watching[0]!.Dispose();
                watching[0] = null;

                using HttpResponseMessage admitted = await client.GetAsync(
                    $"/api/v1/cameras/{uuid}/stream.mjpeg", TestContext.Current.CancellationToken);

                admitted.StatusCode.Should().Be(HttpStatusCode.BadGateway,
                                                "one viewer left, so the request got past the ceiling to the relay");
            }
            finally
            {
                Release(watching);
            }
        }
    }

    /// <summary>
    /// The ceiling is the account's, so another account watching its own camera is unaffected by one
    /// that is at its limit.
    /// </summary>
    [Fact]
    public async Task AnotherAccountIsNotRefusedByTheFirstAccountsStreams()
    {
        (HSUser full, HttpClient fullClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "full@example.com");
        (HSUser other, HttpClient otherClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "other@example.com");

        using (fullClient)
        using (otherClient)
        {
            Guid theirs = await AddCameraAsync(other);
            List<IDisposable?> watching = Hold(full.Id, 5);

            try
            {
                using HttpResponseMessage answer = await otherClient.GetAsync(
                    $"/api/v1/cameras/{theirs}/stream.mjpeg", TestContext.Current.CancellationToken);

                answer.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            }
            finally
            {
                Release(watching);
            }
        }
    }

    /// <summary>
    /// A stream that never started must not keep its place: otherwise five cameras that produced no
    /// pictures would lock an account out of live view until the process restarted.
    /// </summary>
    [Fact]
    public async Task AStreamThatFailsToOpenGivesItsPlaceBack()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "failing@example.com");

        using (client)
        {
            Guid uuid = await AddCameraAsync(user);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                using HttpResponseMessage answer = await client.GetAsync(
                    $"/api/v1/cameras/{uuid}/stream.mjpeg", TestContext.Current.CancellationToken);

                answer.StatusCode.Should().Be(HttpStatusCode.BadGateway, "attempt {0} is not refused", attempt + 1);
            }

            List<IDisposable?> watching = Hold(user.Id, 5);

            try
            {
                watching.Should().NotContainNulls("all five places are free after six failed opens");
            }
            finally
            {
                Release(watching);
            }
        }
    }

    [Fact]
    public async Task ACameraTheCallerCannotSeeUsesNoPlace()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owner@example.com");
        (HSUser stranger, HttpClient strangerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "stranger@example.com");

        using (ownerClient)
        using (strangerClient)
        {
            Guid uuid = await AddCameraAsync(owner);
            List<IDisposable?> watching = Hold(stranger.Id, 5);

            try
            {
                using HttpResponseMessage answer = await strangerClient.GetAsync(
                    $"/api/v1/cameras/{uuid}/stream.mjpeg", TestContext.Current.CancellationToken);

                answer.StatusCode.Should().Be(HttpStatusCode.NotFound,
                                              "access is decided first, so a full account learns nothing new about a camera it cannot see");
            }
            finally
            {
                Release(watching);
            }
        }
    }

    private static void Release(List<IDisposable?> leases)
    {
        foreach (IDisposable? lease in leases)
        {
            lease?.Dispose();
        }
    }

    private List<IDisposable?> Hold(long userId, int count)
    {
        MjpegStreamLimiter limiter = _factory.Services.GetRequiredService<MjpegStreamLimiter>();

        return [.. Enumerable.Range(0, count).Select(_ => limiter.TryAcquire(userId))];
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

    /// <summary>Every camera carries JPEG, so every one is watched over the MJPEG relay.</summary>
    private sealed class JpegOnly : ICameraCodecProbe
    {
        public Task<IReadOnlySet<string>?> ProbeCodecsAsync(Guid streamName, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string> { "JPEG" });
        }
    }
}
