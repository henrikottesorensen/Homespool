using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What happens to a camera's password when the camera is edited, through the real Cameras page -
/// and that the page's list shows none of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The edit form never shows the password</b> - it renders a placeholder in its place - and it
/// posts back whatever it showed. So the ordinary edit, one that changes the name or the address and
/// leaves the password alone, arrives at the service carrying the placeholder where the password
/// was. Storing that would break the camera on the next save without anything looking wrong on the
/// page. The stored password has to survive, and a typed one has to replace it.
/// </para>
/// <para>
/// The camera is added through the page rather than seeded, so the password is split and protected
/// exactly as a real one is; the sidecar credential is configured and its address answers nothing,
/// so the save reaches the database and the registration fails as it would with the sidecar down.
/// </para>
/// </remarks>
public sealed class CameraPasswordEditTests : IAsyncLifetime
{
    private const string Address = "rtsp://192.0.2.1/live";
    private const string OriginalSource = "rtsp://cam:original-secret@192.0.2.1/live"; // betterleaks:allow - a test fixture for a camera that does not exist

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-password");
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
    /// The password is not on the edit form, and the form is posted back as shown: the camera keeps the
    /// password it had, and the rest of the edit is applied.
    /// </summary>
    [Fact]
    public async Task AnEditThatLeavesThePasswordAloneKeepsIt()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-password-keeper@example.com");

        using (client)
        {
            Guid uuid = await AddCameraAsync(client, user);

            string shown = await SourceShownOnEditFormAsync(client, uuid);
            shown.Should().NotContain("original-secret", "the form must not carry the password to the browser");
            shown.Should().Contain(CameraSourceDisplay.HiddenPassword, "the placeholder stands where the password was");

            using HttpResponseMessage response = await PostEditAsync(client, uuid, "renamed", shown);

            Camera camera = await StoredCameraAsync(uuid);
            camera.Name.Should().Be("renamed", "the edit itself must have been applied, or keeping the password proves nothing");
            camera.Source.Should().Be(Address, "the column holds the address alone once the password is split out");
            Reveal(camera).Should().Be(OriginalSource, "posting the placeholder back means the password was left alone");
        }
    }

    /// <summary>
    /// A password typed into the form replaces the stored one.
    /// </summary>
    [Fact]
    public async Task ATypedPasswordReplacesTheStoredOne()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-password-changer@example.com");

        using (client)
        {
            Guid uuid = await AddCameraAsync(client, user);

            using HttpResponseMessage response = await PostEditAsync(
                client, uuid, "rekeyed", "rtsp://cam:replacement-secret@192.0.2.1/live"); // betterleaks:allow - a test fixture for a camera that does not exist

            Camera camera = await StoredCameraAsync(uuid);
            camera.Name.Should().Be("rekeyed");
            Reveal(camera).Should().Be("rtsp://cam:replacement-secret@192.0.2.1/live", // betterleaks:allow - as above
                                                     "a real password in the form is somebody changing it");
        }
    }

    /// <summary>
    /// The placeholder posted with a different host is refused: the stored password is not handed to
    /// a server it was never saved for, and the camera is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task ThePlaceholderIsRefusedForADifferentHost()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-password-mover@example.com");

        using (client)
        {
            Guid uuid = await AddCameraAsync(client, user);

            using HttpResponseMessage response = await PostEditAsync(
                client, uuid, "moved", $"rtsp://cam:{CameraSourceDisplay.HiddenPassword}@198.51.100.9/live");

            string error = await ErrorShownAsync(client);

            Camera camera = await StoredCameraAsync(uuid);
            camera.Name.Should().Be("with password", "a refused edit applies nothing");
            Reveal(camera).Should().Be(OriginalSource, "the stored password stays with the camera it was saved for");
            error.Should().NotBeEmpty("the editor has to be told to type the password");
            error.Should().NotContain("original-secret");
        }
    }

    /// <summary>
    /// A password the camera takes in its query string is nowhere on the list a view-only member reads.
    /// </summary>
    /// <remarks>
    /// Not split out and protected the way a userinfo password is - it stays in the stored address -
    /// so the list is the only thing between it and every member holding <c>ViewCamera</c>.
    /// </remarks>
    [Fact]
    public async Task AViewerIsNotShownAPasswordFromTheQueryString()
    {
        const string querySource = "http://192.0.2.1:88/cgi-bin/CGIProxy.fcgi?cmd=snapPicture2&usr=cam&pwd=query-secret"; // betterleaks:allow - a test fixture for a camera that does not exist

        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-query-owner@example.com");
        (HSUser viewer, HttpClient viewerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-query-viewer@example.com");

        using (ownerClient)
        using (viewerClient)
        {
            Camera camera = await AddCameraThroughPageAsync(ownerClient, owner, querySource);
            camera.Source.Should().Contain("query-secret", "the password is still in the stored address, which is why the list matters");

            (int teamId, _) = await TeamOfAsync(owner);
            await JoinAsViewerAsync(viewer, teamId);

            string list = await GetPageAsync(viewerClient, "/Cameras");

            list.Should().Contain("with password", "the viewer must be shown the camera, or its password being absent proves nothing");
            list.Should().Contain("http://192.0.2.1:88", "the viewer is still told which device this is");
            list.Should().NotContain("query-secret");
            list.Should().NotContain("usr=cam", "a user name is half a credential");
        }
    }

    /// <summary>Adds a camera with a password through the page, and returns its uuid.</summary>
    private async Task<Guid> AddCameraAsync(HttpClient client, HSUser user)
    {
        Camera camera = await AddCameraThroughPageAsync(client, user, OriginalSource);
        camera.CredentialSecret.Should().NotBeNull("the page's own path splits the password out and protects it");

        return camera.Uuid;
    }

    /// <summary>Adds a camera to the account's default team through the page, and returns it as stored.</summary>
    private async Task<Camera> AddCameraThroughPageAsync(HttpClient client, HSUser user, string source)
    {
        string page = await GetPageAsync(client, "/Cameras");
        (int teamId, Guid teamUuid) = await TeamOfAsync(user);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("name", "with password"),
            new("source", source),
            new("teamUuid", teamUuid.ToString()),
            new("printerUuid", string.Empty),
        ]);

        using HttpResponseMessage response =
            await client.PostAsync("/Cameras?handler=AddNetwork", form, TestContext.Current.CancellationToken);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras
                            .AsNoTracking()
                            .SingleAsync(c => c.TeamId == teamId, TestContext.Current.CancellationToken);
    }

    /// <summary>Makes an account a view-only member of a team it was not in.</summary>
    private async Task JoinAsViewerAsync(HSUser user, int teamId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = user.Id,
            Capabilities = CapabilitySet.Format(CapabilityPresets.Viewer),
            IsDefault = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostEditAsync(HttpClient client, Guid uuid, string name, string source)
    {
        string page = await GetPageAsync(client, $"/Cameras?edit={uuid}");

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
            new("name", name),
            new("source", source),
            new("printerUuid", string.Empty),
        ]);

        return await client.PostAsync("/Cameras?handler=Edit", form, TestContext.Current.CancellationToken);
    }

    /// <summary>The source exactly as the edit form's input carries it, which is what a browser posts back.</summary>
    private static async Task<string> SourceShownOnEditFormAsync(HttpClient client, Guid uuid)
    {
        string page = await GetPageAsync(client, $"/Cameras?edit={uuid}");

        Match input = Regex.Match(page, """id="edit-source"[^>]*value="([^"]*)"[^>]*>""");
        input.Success.Should().BeTrue("the edit form must render the source input");

        return WebUtility.HtmlDecode(input.Groups[1].Value);
    }

    private async Task<Camera> StoredCameraAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras
                            .AsNoTracking()
                            .SingleAsync(camera => camera.Uuid == uuid, TestContext.Current.CancellationToken);
    }

    /// <summary>The source with its password back in, as the sidecar would be handed it.</summary>
    private string Reveal(Camera camera)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<CameraCredentialProtector>().Reveal(camera);
    }

    /// <summary>The account's default team: its id for the database, its uuid for the form.</summary>
    private async Task<(int id, Guid uuid)> TeamOfAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = await context.TeamMembers
                                  .Where(member => member.UserId == user.Id)
                                  .Select(member => member.TeamId)
                                  .FirstAsync(TestContext.Current.CancellationToken);
        Guid teamUuid = await context.Teams
                                     .Where(team => team.Id == teamId)
                                     .Select(team => team.Uuid)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        return (teamId, teamUuid);
    }

    /// <summary>The refusal the redirect carried, read off the page it lands on.</summary>
    private static async Task<string> ErrorShownAsync(HttpClient client)
    {
        Match match = Regex.Match(await GetPageAsync(client, "/Cameras"), """alert-danger" role="alert">([^<]*)<""");

        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
    }

    private static async Task<string> GetPageAsync(HttpClient client, string path)
    {
        return await (await client.GetAsync(path, TestContext.Current.CancellationToken))
                     .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
