using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Which printer a camera may be pointed at, through the real Cameras page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted on the database rather than on the page.</b> The printer's name is rendered beside the
/// camera for everyone on the camera's team, so what matters is whether the binding was stored, not
/// what the page said about it.
/// </para>
/// <para>
/// <b>The sidecar credential is configured and its address points at nothing.</b> Without the
/// credential <c>CreateAsync</c> refuses before the printer is ever considered, and a refusal test
/// would pass on a codebase with no check in it. With it, a save the check lets through reaches the
/// database and then fails to register, which is exactly the row the refusal cases must not leave.
/// </para>
/// </remarks>
public sealed class CameraPrinterBindingTests : IAsyncLifetime
{
    private const string Source = "rtsp://192.0.2.1/live";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-printer");
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
    /// A printer on the camera's own team is stored against it - the case the refusals below must not
    /// swallow.
    /// </summary>
    [Fact]
    public async Task APrinterOnTheCamerasTeamIsBound()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-own@example.com");

        using (client)
        {
            (int teamId, Printer printer) = await SeedPrinterAsync(user, "Own printer");

            using HttpResponseMessage response = await PostAddNetworkAsync(client, teamId, printer.Uuid.ToString());

            (await StoredPrinterIdsAsync()).Should().Equal([printer.Id], "a printer of the camera's own team is what the select is for");
        }
    }

    /// <summary>
    /// Another team's printer and a printer that does not exist are both refused, nothing is stored,
    /// and the two refusals read the same.
    /// </summary>
    /// <remarks>
    /// The list renders the bound printer's name, so a stored binding to another team's printer
    /// discloses it - and a refusal that differed from the unknown-uuid one would confirm it exists.
    /// </remarks>
    [Fact]
    public async Task AnotherTeamsPrinterIsRefusedInTheWordsAnUnknownOneIs()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-adder@example.com");
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-printer-owner@example.com");

        using (client)
        using (ownerClient)
        {
            int teamId = await TeamIdAsync(user);
            (_, Printer theirs) = await SeedPrinterAsync(owner, "Somebody else's printer");

            using HttpResponseMessage foreign = await PostAddNetworkAsync(client, teamId, theirs.Uuid.ToString());
            string foreignError = await ErrorShownAsync(client);

            using HttpResponseMessage unknown = await PostAddNetworkAsync(client, teamId, Guid.NewGuid().ToString());
            string unknownError = await ErrorShownAsync(client);

            (await StoredPrinterIdsAsync()).Should().BeEmpty("neither refusal may leave a camera behind");
            foreignError.Should().NotBeEmpty();
            foreignError.Should().Be(unknownError, "a printer on another team must read exactly like one that does not exist");
        }
    }

    /// <summary>
    /// A team the saver is not in and a team uuid that names nothing are both refused, nothing is
    /// stored, and the two refusals read the same.
    /// </summary>
    /// <remarks>
    /// The form names a team by its uuid, so a refusal that differed between the two would confirm
    /// which uuids name somebody else's team.
    /// </remarks>
    [Fact]
    public async Task AnotherTeamIsRefusedInTheWordsAnUnknownOneIs()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-team-adder@example.com");
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-team-owner@example.com");

        using (client)
        using (ownerClient)
        {
            Guid theirTeam = await TeamUuidAsync(await TeamIdAsync(owner));

            using HttpResponseMessage foreign = await PostAddNetworkAsync(client, theirTeam, printerUuid: string.Empty);
            string foreignError = await ErrorShownAsync(client);

            using HttpResponseMessage unknown = await PostAddNetworkAsync(client, Guid.NewGuid(), printerUuid: string.Empty);
            string unknownError = await ErrorShownAsync(client);

            (await StoredPrinterIdsAsync()).Should().BeEmpty("neither refusal may leave a camera behind");
            foreignError.Should().NotBeEmpty();
            foreignError.Should().Be(unknownError, "another team must read exactly like a team that does not exist");
        }
    }

    /// <summary>
    /// A printer the saver can see is still refused when it belongs to a team other than the camera's.
    /// </summary>
    /// <remarks>
    /// The saver is a member of both teams, so the only thing that can refuse this is the team rule -
    /// the case above is refused before that rule is ever asked.
    /// </remarks>
    [Fact]
    public async Task APrinterTheSaverCanSeeOnAnotherTeamIsRefused()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-two-teams@example.com");
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-other-team@example.com");

        using (client)
        using (ownerClient)
        {
            int teamId = await TeamIdAsync(user);
            (int otherTeamId, Printer visible) = await SeedPrinterAsync(owner, "Visible elsewhere");

            await JoinAsync(user, otherTeamId);

            using HttpResponseMessage response = await PostAddNetworkAsync(client, teamId, visible.Uuid.ToString());

            (await StoredPrinterIdsAsync()).Should().BeEmpty("the name would be shown to the camera's team, who cannot see the printer");
        }
    }

    /// <summary>
    /// A printer on the camera's own team is refused to somebody who may manage cameras there but not
    /// see printers.
    /// </summary>
    /// <remarks>
    /// Managing a camera implies seeing cameras, not printers, so this membership is one a team can
    /// grant - and saving the binding would hand its holder the printer's name in the camera list.
    /// </remarks>
    [Fact]
    public async Task APrinterTheSaverCannotSeeOnTheCamerasTeamIsRefused()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-no-printers@example.com");

        using (client)
        {
            (int teamId, Printer hidden) = await SeedPrinterAsync(user, "Hidden on my team");

            await RestrictToCamerasAsync(user, teamId);

            using HttpResponseMessage response = await PostAddNetworkAsync(client, teamId, hidden.Uuid.ToString());

            (await StoredPrinterIdsAsync()).Should().BeEmpty("a camera must not be a way to read a printer the saver cannot see");
        }
    }

    /// <summary>
    /// Editing a camera onto another team's printer leaves its binding as it was.
    /// </summary>
    [Fact]
    public async Task EditingOntoAnotherTeamsPrinterChangesNothing()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-editor@example.com");
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-edit-owner@example.com");

        using (client)
        using (ownerClient)
        {
            (int teamId, Printer mine) = await SeedPrinterAsync(user, "Mine");
            (_, Printer theirs) = await SeedPrinterAsync(owner, "Theirs");

            Guid camera = await SeedCameraAsync(teamId, mine.Id);

            string page = await GetPageAsync(client);

            using FormUrlEncodedContent form = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
                new("uuid", camera.ToString()),
                new("name", "moved"),
                new("source", Source),
                new("printerUuid", theirs.Uuid.ToString()),
            ]);

            using HttpResponseMessage response =
                await client.PostAsync("/Cameras?handler=Edit", form, TestContext.Current.CancellationToken);

            (await StoredPrinterIdsAsync()).Should().Equal([mine.Id], "a refused edit keeps the binding it had");
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

    private async Task RestrictToCamerasAsync(HSUser user, int teamId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == user.Id && member.TeamId == teamId,
                                                          TestContext.Current.CancellationToken);

        membership.Capabilities = CapabilitySet.Format([Capability.ManageCamera]);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task JoinAsync(HSUser user, int teamId)
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

    private async Task<(int teamId, Printer printer)> SeedPrinterAsync(HSUser user, string name)
    {
        int teamId = await TeamIdAsync(user);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = name };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (teamId, printer);
    }

    /// <summary>
    /// Written straight to the database: the page's own path registers with a sidecar that is not
    /// here, and the edit is what is on trial.
    /// </summary>
    private async Task<Guid> SeedCameraAsync(int teamId, int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Seeded camera",
            Source = Source,
            TeamId = teamId,
            PrinterId = printerId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Cameras.Add(camera);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return camera.Uuid;
    }

    private async Task<int?[]> StoredPrinterIdsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras.Select(camera => camera.PrinterId).ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetPageAsync(HttpClient client)
    {
        return await (await client.GetAsync("/Cameras", TestContext.Current.CancellationToken))
                     .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The refusal the redirect carried, read off the page it lands on.</summary>
    private static async Task<string> ErrorShownAsync(HttpClient client)
    {
        Match match = Regex.Match(await GetPageAsync(client), """alert-danger" role="alert">([^<]*)<""");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>The uuid the Cameras form names a team by.</summary>
    private async Task<Guid> TeamUuidAsync(int teamId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Teams
                            .Where(team => team.Id == teamId)
                            .Select(team => team.Uuid)
                            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PostAddNetworkAsync(HttpClient client, int teamId, string printerUuid)
    {
        return await PostAddNetworkAsync(client, await TeamUuidAsync(teamId), printerUuid);
    }

    private static async Task<HttpResponseMessage> PostAddNetworkAsync(HttpClient client, Guid teamUuid, string printerUuid)
    {
        string page = await GetPageAsync(client);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("name", "bound"),
            new("source", Source),
            new("teamUuid", teamUuid.ToString()),
            new("printerUuid", printerUuid),
        ]);

        return await client.PostAsync("/Cameras?handler=AddNetwork", form, TestContext.Current.CancellationToken);
    }
}
