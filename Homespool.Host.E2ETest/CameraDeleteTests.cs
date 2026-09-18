using System;
using System.Linq;
using System.Net.Http;
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
/// Who may remove a camera, through the real Cameras page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Removing an attached camera is releasing a device</b>, and the rule that only an administrator
/// may claim one has to hold on the way out too: a team member who could not have plugged the camera
/// in must not be able to hand its device back either. That refusal is the one thing here a
/// permission audit would care about, so it gets both halves - the refusal and the administrator
/// who is allowed through.
/// </para>
/// <para>
/// <b>The sidecar credential is configured and its address points at nothing</b>, the same setup as
/// <see cref="CameraPrinterBindingTests"/>, so a removal that gets past the checks reaches the
/// database. <b>What becomes of the sidecar's own stream is not visible from here</b> - nothing in
/// this environment answers as a sidecar - so these cases say who may remove a camera and nothing
/// about whether the stream server was left holding its device.
/// </para>
/// </remarks>
public sealed class CameraDeleteTests : IAsyncLifetime
{
    private const string NetworkSource = "rtsp://192.0.2.1/live";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-delete");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _factory.ConfigurationOverrides["Cameras:ApiUsername"] = "homespool";
        _factory.ConfigurationOverrides["Cameras:ApiPassword"] = "not-a-real-password"; // betterleaks:allow - the sidecar address below answers nothing
        _factory.ConfigurationOverrides["Cameras:StreamServerBaseUrl"] = "http://127.0.0.1:9";

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
    /// A team member who manages cameras removes one of the team's network cameras.
    /// </summary>
    [Fact]
    public async Task AManagerRemovesANetworkCamera()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-remover@example.com");

        using (client)
        {
            Guid uuid = await SeedCameraAsync(await TeamIdAsync(user), NetworkSource);

            using HttpResponseMessage response = await PostDeleteAsync(client, uuid);

            (await StoredUuidsAsync()).Should().BeEmpty("a network camera belongs to its team, and a manager may remove it");
        }
    }

    /// <summary>
    /// Another team's camera is left alone, exactly as an unknown uuid would be.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesCameraIsNotRemoved()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-owner@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-stranger@example.com");

        using (ownerClient)
        using (stranger)
        {
            Guid uuid = await SeedCameraAsync(await TeamIdAsync(owner), NetworkSource);

            using HttpResponseMessage response = await PostDeleteAsync(stranger, uuid);

            (await StoredUuidsAsync()).Should().Equal([uuid], "a uuid is not a permission");
        }
    }

    /// <summary>
    /// A manager who is not an administrator cannot remove an attached camera - releasing the
    /// device is the administrator's call, the same as claiming it was.
    /// </summary>
    [Fact]
    public async Task ANonAdministratorCannotFreeAnAttachedCamera()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-manager@example.com");

        using (client)
        {
            Guid uuid = await SeedCameraAsync(await TeamIdAsync(user), LocalCameraDevices.SourceFor("usb-camera-video-index0"));

            using HttpResponseMessage response = await PostDeleteAsync(client, uuid);

            (await StoredUuidsAsync()).Should().Equal([uuid],
                                                      "a team member who could not have claimed the device must not be able to release it");
        }
    }

    /// <summary>
    /// An administrator removes an attached camera, which hands its device back.
    /// </summary>
    [Fact]
    public async Task AnAdministratorFreesAnAttachedCamera()
    {
        (HSUser admin, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-admin@example.com", AdminBootstrap.AdminRole);

        using (client)
        {
            Guid uuid = await SeedCameraAsync(await TeamIdAsync(admin), LocalCameraDevices.SourceFor("usb-camera-video-index0"));

            using HttpResponseMessage response = await PostDeleteAsync(client, uuid);

            (await StoredUuidsAsync()).Should().BeEmpty("the administrator who could claim the device is who may release it");
        }
    }

    private async Task<int> TeamIdAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.TeamMembers
                            .Where(member => member.UserId == user.Id)
                            .Select(member => member.TeamId)
                            .FirstAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Written straight to the database: an attached camera cannot be added here, since the device it
    /// names is not plugged into the test host, and the removal is what is on trial.
    /// </summary>
    private async Task<Guid> SeedCameraAsync(int teamId, string source)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Seeded camera",
            Source = source,
            TeamId = teamId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Cameras.Add(camera);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }

    private async Task<Guid[]> StoredUuidsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras.Select(camera => camera.Uuid).ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostDeleteAsync(HttpClient client, Guid uuid)
    {
        string page = await (await client.GetAsync("/Cameras", TestContext.Current.CancellationToken))
                            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
        ]);

        return await client.PostAsync("/Cameras?handler=Delete", form, TestContext.Current.CancellationToken);
    }
}
