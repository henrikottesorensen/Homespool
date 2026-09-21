using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>GET /api/v1/cameras</c>: which cameras a caller may watch, and what each one sends.
/// </summary>
/// <remarks>
/// <para>
/// Cameras are written straight to the database, as <c>CameraStreamLimitTests</c> writes them: going
/// through <c>CameraService</c> would register them with a stream server that is not running here.
/// The codec probe is a script, so a test decides what each camera answers and can count whether the
/// listing asked at all.
/// </para>
/// <para>
/// <b>The listing's defining property is what it leaves out</b> - a camera's address and credentials -
/// so that is asserted on the raw body, where a leak would be, rather than on the fields a parser
/// happens to read.
/// </para>
/// </remarks>
public sealed class CameraListEndpointTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camlist");
    private readonly ScriptedProbe _probe = new();
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICameraCodecProbe>(_probe);
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

    /// <summary>
    /// The caller's cameras come back by handle and name, and nothing of how they are reached.
    /// </summary>
    [Fact]
    public async Task TheCallersCamerasAreListedWithoutTheirAddressOrCredentials()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-owner@example.com");

        using (client)
        {
            Guid named = await AddCameraAsync(user.Id, "Bed cam", source: "rtsp://192.0.2.50:554/live",
                                              credentialUser: "camadmin");
            Guid unnamed = await AddCameraAsync(user.Id, name: null, source: "ffmpeg:device?video=/dev/v4l/by-id/usb-cam");
            Guid team = await TeamUuidAsync(user.Id);

            // Act
            (JsonElement[] cameras, string raw) = await ListAsync(client);

            // Assert
            cameras.Select(camera => camera.GetProperty("uuid").GetGuid()).Should().BeEquivalentTo([named, unnamed]);
            cameras.Should().OnlyContain(camera => camera.GetProperty("teamUuid").GetGuid() == team);

            JsonElement anonymous = cameras.Single(camera => camera.GetProperty("uuid").GetGuid() == unnamed);
            anonymous.GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null, "nobody named it");

            raw.Should().NotContain("192.0.2.50").And.NotContain("rtsp").And.NotContain("/dev/v4l")
               .And.NotContain("camadmin").And.NotContainEquivalentOf("source").And.NotContainEquivalentOf("credential");
        }
    }

    /// <summary>Another team's camera is not listed.</summary>
    [Fact]
    public async Task AnotherTeamsCameraIsNotListed()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-keeper@example.com");
        ownerClient.Dispose();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-stranger@example.com");

        using (client)
        {
            await AddCameraAsync(owner.Id, "Theirs");
            Guid mine = await AddCameraAsync(user.Id, "Mine");

            // Act
            (JsonElement[] cameras, _) = await ListAsync(client);

            // Assert
            cameras.Select(camera => camera.GetProperty("uuid").GetGuid()).Should().Equal(mine);
        }
    }

    /// <summary>
    /// A bound printer is named to somebody who may see it, and not to somebody who may see only the
    /// camera; an unbound camera names none.
    /// </summary>
    [Fact]
    public async Task ABoundPrinterIsNamedOnlyToACallerWhoMaySeeIt()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-binder@example.com");

        using (client)
        {
            (int printerId, Guid printerUuid) = await AddPrinterAsync(user.Id);
            Guid bound = await AddCameraAsync(user.Id, "Bound", printerId: printerId);
            Guid unbound = await AddCameraAsync(user.Id, "Unbound");

            // Act
            (JsonElement[] seen, _) = await ListAsync(client);

            await SetCapabilitiesAsync(user.Id, Capability.ViewCamera);
            (JsonElement[] hidden, _) = await ListAsync(client);

            // Assert
            PrinterOf(seen, bound).Should().Be(printerUuid);
            PrinterOf(seen, unbound).Should().BeNull();
            PrinterOf(hidden, bound).Should().BeNull("seeing the camera does not grant seeing its printer");
        }
    }

    /// <summary>
    /// A camera that has answered lists its codecs and transport; one that has not lists null for
    /// both - and the listing asks no camera anything either way.
    /// </summary>
    [Fact]
    public async Task CodecsComeFromWhatIsKnownAndTheListingOpensNoCamera()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-codec@example.com");

        using (client)
        {
            Guid answered = await AddCameraAsync(user.Id, "Answered");
            Guid silent = await AddCameraAsync(user.Id, "Silent");

            _probe.Answer(answered, "JPEG");
            await _factory.Services.GetRequiredService<CameraLiveAvailability>()
                          .HowToWatchAsync(answered, TestContext.Current.CancellationToken);

            int askedBefore = _probe.Calls;

            // Act
            (JsonElement[] cameras, _) = await ListAsync(client);

            // Assert
            _probe.Calls.Should().Be(askedBefore, "a listing must not open cameras");

            JsonElement known = cameras.Single(camera => camera.GetProperty("uuid").GetGuid() == answered);
            known.GetProperty("codecs").EnumerateArray().Select(codec => codec.GetString()).Should().Equal("JPEG");
            known.GetProperty("transport").GetString().Should().Be("mjpeg");

            JsonElement unknown = cameras.Single(camera => camera.GetProperty("uuid").GetGuid() == silent);
            unknown.GetProperty("codecs").ValueKind.Should().Be(JsonValueKind.Null);
            unknown.GetProperty("transport").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>A token whose scope does not name <c>ViewCamera</c> sees no cameras at all.</summary>
    [Fact]
    public async Task ATokenWithoutViewCameraListsNothing()
    {
        // Arrange
        (HSUser user, HttpClient signedIn) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cam-token@example.com");
        signedIn.Dispose();

        await AddCameraAsync(user.Id, "Hidden");

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await MintTokenAsync(user.Id, Capability.ViewPrinter));

        // Act
        (JsonElement[] cameras, _) = await ListAsync(client);

        // Assert
        cameras.Should().BeEmpty();
    }

    private static Guid? PrinterOf(JsonElement[] cameras, Guid camera)
    {
        JsonElement printer = cameras.Single(entry => entry.GetProperty("uuid").GetGuid() == camera)
                                     .GetProperty("printerUuid");

        return printer.ValueKind == JsonValueKind.Null ? null : printer.GetGuid();
    }

    private static async Task<(JsonElement[] cameras, string raw)> ListAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v1/cameras", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument payload = JsonDocument.Parse(raw);

        return ([.. payload.RootElement.EnumerateArray().Select(camera => camera.Clone())], raw);
    }

    private async Task<string> MintTokenAsync(long userId, params Capability[] scope)
    {
        using IServiceScope services = _factory.Services.CreateScope();

        ApiTokenService tokens = services.ServiceProvider.GetRequiredService<ApiTokenService>();
        (_, string plaintext) = await tokens.CreateAsync(userId, "cameras", CapabilitySet.Parse(CapabilitySet.Format(scope)),
                                                         CancellationToken.None);

        return plaintext;
    }

    private async Task SetCapabilitiesAsync(long userId, params Capability[] capabilities)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await database.TeamMembers.FirstAsync(member => member.UserId == userId,
                                                                      TestContext.Current.CancellationToken);
        membership.Capabilities = CapabilitySet.Format(capabilities);

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> TeamUuidAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await database.TeamMembers
                             .Where(member => member.UserId == userId)
                             .Select(member => member.Team!.Uuid)
                             .FirstAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(int id, Guid uuid)> AddPrinterAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await database.TeamMembers.FirstAsync(member => member.UserId == userId,
                                                                      TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Watched",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        database.Printers.Add(printer);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (printer.Id, printer.Uuid);
    }

    private async Task<Guid> AddCameraAsync(long userId,
                                            string? name,
                                            string source = "rtsp://192.0.2.1/live",
                                            string? credentialUser = null,
                                            int? printerId = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await database.TeamMembers.FirstAsync(member => member.UserId == userId,
                                                                      TestContext.Current.CancellationToken);

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = name,
            Source = source,
            CredentialUser = credentialUser,
            TeamId = membership.TeamId,
            PrinterId = printerId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        database.Cameras.Add(camera);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }

    /// <summary>A probe that answers only for cameras a test has scripted, and counts every ask.</summary>
    private sealed class ScriptedProbe : ICameraCodecProbe
    {
        private readonly Dictionary<Guid, IReadOnlySet<string>> _answers = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Answer(Guid camera, params string[] codecs)
        {
            lock (_answers)
            {
                _answers[camera] = new HashSet<string>(codecs);
            }
        }

        public Task<IReadOnlySet<string>?> ProbeCodecsAsync(Guid streamName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            lock (_answers)
            {
                return Task.FromResult(_answers.TryGetValue(streamName, out IReadOnlySet<string>? codecs) ? codecs : null);
            }
        }
    }
}
