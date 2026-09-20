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
/// Who may edit an attached camera onto the network, through the real Cameras page.
/// </summary>
/// <remarks>
/// <para>
/// <b>An edit that replaces an attached camera's source releases its device</b>, exactly as removing
/// the camera does - the stream server is handed the new address for the same camera and lets go of
/// the old one, and the device is offered to the next person adding a camera. So the rule
/// <see cref="CameraDeleteTests"/> pins on the way out has to hold here too: a team member who could
/// not have claimed the device must not be able to hand it back by typing over the source box.
/// </para>
/// <para>
/// <b>The edit form offers that box for every camera its caller may manage</b>, attached or not, so
/// this needs no crafted request. An edit that leaves an attached camera attached was already
/// refused - the source arriving names a device - which is why the source being <i>replaced</i> is
/// what these two cases vary.
/// </para>
/// <para>
/// <b>The sidecar credential is configured and its address points at nothing</b>, the same setup as
/// <see cref="CameraDeleteTests"/>, so a save that gets past the checks reaches the database. What
/// becomes of the stream server's own hold on the device is not visible from here.
/// </para>
/// </remarks>
public sealed class CameraDetachTests : IAsyncLifetime
{
    private const string NetworkSource = "rtsp://192.0.2.1/live";

    private static readonly string AttachedSource = LocalCameraDevices.SourceFor("usb-camera-video-index0");

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-detach");
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
    /// A manager who is not an administrator cannot edit an attached camera onto the network -
    /// freeing the device is the administrator's call, the same as claiming it was.
    /// </summary>
    [Fact]
    public async Task ANonAdministratorCannotFreeAnAttachedCameraByEditingIt()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-detacher@example.com");

        using (client)
        {
            Guid uuid = await SeedAttachedCameraAsync(await TeamIdAsync(user));

            using HttpResponseMessage response = await PostEditAsync(client, uuid, NetworkSource);

            (await StoredSourceAsync(uuid)).Should().Be(AttachedSource,
                                                        "a team member who could not have claimed the device must not be able to release it by editing");
        }
    }

    /// <summary>
    /// An administrator edits an attached camera onto the network, which hands its device back.
    /// </summary>
    /// <remarks>
    /// The refusal above would read the same against a save that failed for any other reason - a
    /// source the policy dislikes, a sidecar that is not there - so this is what makes it measure the
    /// permission.
    /// </remarks>
    [Fact]
    public async Task AnAdministratorFreesAnAttachedCameraByEditingIt()
    {
        (HSUser admin, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-detach-admin@example.com", AdminBootstrap.AdminRole);

        using (client)
        {
            Guid uuid = await SeedAttachedCameraAsync(await TeamIdAsync(admin));

            using HttpResponseMessage response = await PostEditAsync(client, uuid, NetworkSource);

            (await StoredSourceAsync(uuid)).Should().Be(NetworkSource,
                                                        "the administrator who could claim the device is who may release it");
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
    /// names is not plugged into the test host, and the edit is what is on trial.
    /// </summary>
    private async Task<Guid> SeedAttachedCameraAsync(int teamId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Seeded camera",
            Source = AttachedSource,
            TeamId = teamId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Cameras.Add(camera);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }

    private async Task<string?> StoredSourceAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras
                            .Where(camera => camera.Uuid == uuid)
                            .Select(camera => camera.Source)
                            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostEditAsync(HttpClient client, Guid uuid, string source)
    {
        string page = await (await client.GetAsync("/Cameras", TestContext.Current.CancellationToken))
                            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
            new("name", "Seeded camera"),
            new("source", source),
        ]);

        return await client.PostAsync("/Cameras?handler=Edit", form, TestContext.Current.CancellationToken);
    }
}
