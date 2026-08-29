using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What happens when the camera sidecar has no credential: nothing is sent to it, at all.
/// </summary>
/// <remarks>
/// <para>
/// The security property under test is not "a header is omitted" but "no request is made", so the
/// assertions are on the <see cref="IHttpClientFactory"/> never being asked for a client. A test that
/// checked the header instead would still pass if the call went out unauthenticated, which is the
/// arrangement this rule exists to end - see <see cref="CameraOptions.IsAuthenticated"/>.
/// </para>
/// </remarks>
public sealed class CameraCredentialTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-camcred-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Half a credential is worse than none - measured 2026-08-09, a username with an empty password
    /// turns go2rtc's authentication on with an empty key and locks Homespool out with everyone else.
    /// So the predicate demands both halves rather than either.
    /// </summary>
    [Theory]
    [InlineData("", "", false)]
    [InlineData("homespool", "", false)]
    [InlineData("", "secret", false)]
    [InlineData("homespool", "secret", true)]
    public void BothHalvesAreNeededOrTheSidecarCountsAsUncredentialed(string user, string password, bool expected)
    {
        CameraOptions options = new() { ApiUsername = user, ApiPassword = password };

        options.IsAuthenticated.Should().Be(expected);
    }

    [Fact]
    public void NoFrameAddressIsOfferedWithoutACredential()
    {
        (Go2RtcClient client, IHttpClientFactory factory) = Build(credentialed: false);

        client.FrameUrl(Guid.NewGuid()).Should().BeNull("an address nobody may fetch is worse than none");

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public void AFrameAddressIsOfferedOnceCredentialed()
    {
        (Go2RtcClient client, _) = Build(credentialed: true);

        Guid stream = Guid.NewGuid();

        client.FrameUrl(stream)!.ToString().Should().Contain(stream.ToString("D"));
    }

    /// <summary>
    /// The one that closes the hole: registering a source is how a camera address reaches the
    /// sidecar, so refusing here is what stops an uncredentialed sidecar being handed one.
    /// </summary>
    [Fact]
    public async Task NoSourceIsRegisteredWithoutACredential()
    {
        (Go2RtcClient client, IHttpClientFactory factory) = Build(credentialed: false);

        bool registered = await client.PutStreamAsync(
            Guid.NewGuid(), "http://go2rtc:1984/api/stream.mjpeg?src=exec:whoami", CancellationToken.None);

        registered.Should().BeFalse();

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task NothingIsDeletedWithoutACredential()
    {
        (Go2RtcClient client, IHttpClientFactory factory) = Build(credentialed: false);

        await client.DeleteStreamAsync(Guid.NewGuid(), CancellationToken.None);

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    /// <summary>
    /// Null rather than an empty set, so the reconciler reads it as "could not be asked" and does
    /// nothing - an empty set would mean "every camera is missing" and start a re-registration sweep
    /// against a sidecar this deployment has decided not to talk to.
    /// </summary>
    [Fact]
    public async Task TheStreamListIsUnaskableWithoutACredential()
    {
        (Go2RtcClient client, IHttpClientFactory factory) = Build(credentialed: false);

        IReadOnlySet<string>? names = await client.ListStreamNamesAsync(CancellationToken.None);

        names.Should().BeNull();

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    /// <summary>
    /// The credential crosses two encodings - plain YAML to this process, JSON on the sidecar's
    /// command line - and only some values survive both. The backslash row is the dangerous one: it
    /// does not fail, it arrives different.
    /// </summary>
    [Theory]
    [InlineData("homespool", "Zm9vYmFyYmF6cXV4L4+9", true)]
    [InlineData("homespool", "sim=ple+/9", true)]
    [InlineData("homespool", "has\"quote", false)]
    [InlineData("homespool", "has\\back", false)]
    [InlineData("has\\slash", "secret", false)]
    public void OnlyACredentialThatSurvivesBothEncodingsIsUsable(string user, string password, bool expected)
    {
        CameraOptions options = new() { ApiUsername = user, ApiPassword = password };

        options.CredentialSurvivesTransport.Should().Be(expected);
    }

    /// <summary>
    /// The state that looks healthiest and is not: set, held, and silently different at the far end.
    /// </summary>
    [Fact]
    public async Task TheHealthCheckReportsACredentialThatCannotSurviveTheTrip()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        CameraCredentialHealthCheck check = new(
            TestOptions.Monitor(new CameraOptions { ApiUsername = "homespool", ApiPassword = "has\\back" }),
            context);

        HealthCheckResult result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded,
                                  "every camera answers 401 while both halves look correctly configured");
        result.Description.Should().Contain("backslash");
    }

    [Fact]
    public async Task TheHealthCheckIsQuietWhenNoCameraIsConfigured()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        HealthCheckResult result = await CheckAsync(context, credentialed: false);

        result.Status.Should().Be(HealthStatus.Healthy,
                                  "most deployments have no camera, and a banner about a credential they have no "
                                  + "use for is how people learn to ignore banners");
    }

    [Fact]
    public async Task TheHealthCheckReportsCamerasThatCannotWork()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(context, credentialed: false);

        result.Status.Should().Be(HealthStatus.Degraded,
                                  "a configured camera that will never produce a picture is the case an "
                                  + "administrator has to be told about");
        result.Description.Should().Contain("GO2RTC_PASSWORD");
    }

    [Fact]
    public async Task TheHealthCheckIsQuietOnceCredentialed()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await AddCameraAsync(context);

        HealthCheckResult result = await CheckAsync(context, credentialed: true);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static async Task<HealthCheckResult> CheckAsync(HomespoolDbContext context, bool credentialed)
    {
        CameraCredentialHealthCheck check = new(TestOptions.Monitor(OptionsFor(credentialed)), context);

        return await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    private static async Task AddCameraAsync(HomespoolDbContext context)
    {
        Team team = new() { Name = "Workshop", CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Cameras.Add(new Camera
        {
            Uuid = Guid.NewGuid(),
            Name = "Bed",
            Source = "rtsp://192.0.2.1/live",
            TeamId = team.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static CameraOptions OptionsFor(bool credentialed)
    {
        return new CameraOptions
        {
            ApiUsername = credentialed ? "homespool" : string.Empty,
            ApiPassword = credentialed ? "secret" : string.Empty,
        };
    }

    private static (Go2RtcClient client, IHttpClientFactory factory) Build(bool credentialed)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        Go2RtcClient client = new(factory,
                                  TestOptions.Monitor(OptionsFor(credentialed)),
                                  NullLogger<Go2RtcClient>.Instance);

        return (client, factory);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
