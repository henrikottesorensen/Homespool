using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The Files page's two dispatch buttons - Queue and Send - through the real form posts, each once
/// as somebody permitted and once as somebody whose membership does not grant the act.
/// </summary>
/// <remarks>
/// <para>
/// <b>These handlers had no test driving them as a browser does.</b> The services underneath are
/// tested directly, and a rule whose every test addresses the rule can be left uncalled by the
/// handler without anything noticing - deleting a gate's call sites has left this suite green
/// before. So what is asserted here is the observable outcome: the queue row that was or was not
/// stored, and the command that did or did not reach a connected printer.
/// </para>
/// <para>
/// <b>The refused caller can see the printer</b>, which is what makes the refusal the permission's
/// rather than the resolver's: a Viewer membership renders the page, offers the printer in the
/// selector, and fails only at the gate the test is about.
/// </para>
/// </remarks>
public sealed class FilesPageDispatchTests : IAsyncLifetime
{
    private const string FileContent = "G28 ; home\n";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("files-dispatch");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

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
    /// The Queue button writes a row for the caller's file on the caller's printer - the case the
    /// refusal below must not swallow.
    /// </summary>
    [Fact]
    public async Task QueueingThroughThePageStoresTheRow()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "page-queuer@example.com");

        using (client)
        {
            Guid uuid = await SeedPrinterAsync(user.Id);
            await UploadAsync(client, "benchy.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(client, "Queue", "benchy.gcode", uuid);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedFileNamesAsync()).Should().Equal(["benchy.gcode"],
                                                          "queueing through the page is writing this row");
        }
    }

    /// <summary>
    /// A member who may only view the printer queues nothing, and is told so on the page.
    /// </summary>
    /// <remarks>
    /// The file exists and the printer is in the member's own selector, so the only thing that can
    /// refuse this is the capability check - a missing file or an invisible printer would produce
    /// the same empty table for reasons this test is not about.
    /// </remarks>
    [Fact]
    public async Task AMemberWithoutPrintQueuesNothing()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "page-queue-owner@example.com");
        (HSUser viewer, HttpClient viewerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "page-queue-viewer@example.com");

        using (ownerClient)
        using (viewerClient)
        {
            Guid uuid = await SeedPrinterAsync(owner.Id);
            await JoinAsync(viewer.Id, await TeamOfAsync(uuid), CapabilityPresets.Viewer);
            await UploadAsync(viewerClient, "hopeful.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(viewerClient, "Queue", "hopeful.gcode", uuid);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect, "the page answers refusals as a message, not a status");

            (await QueuedFileNamesAsync()).Should().BeEmpty("a Viewer membership must not put work on the printer");
            (await StatusShownAsync(viewerClient, uuid)).Should().Contain("not use it",
                                                                          "the refusal must reach the person, not only a log");
        }
    }

    /// <summary>
    /// The Send button moves the caller's bytes to a connected printer, verified by what arrived
    /// rather than by what the page claimed.
    /// </summary>
    [Fact]
    public async Task SendingThroughThePageReachesThePrinter()
    {
        (Guid uuid, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            await UploadAsync(client, "benchy.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(client, "Send", "benchy.gcode", uuid);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await StatusShownAsync(client, uuid)).Should().Contain("Sending");

            // The printer pulls the bytes after the command is acknowledged, so the arrival is
            // awaited rather than assumed - and the content is the assertion, because a transfer
            // that ran and corrupted would satisfy anything weaker.
            FakeTransfer transfer = await WaitForTransferAsync(fake);

            transfer.IsComplete.Should().BeTrue();
            transfer.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes(FileContent),
                                                      "what the printer received must be what was uploaded");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A member who may only view the printer sends nothing: the refusal happens before any command
    /// leaves, and the connected printer is what proves it.
    /// </summary>
    /// <remarks>
    /// The printer is connected on purpose. Without one, this handler would refuse with "not
    /// connected" a step later, and the test would pass against a codebase with the permission gate
    /// deleted - the fixture question <c>e2e</c> tests keep having to answer.
    /// </remarks>
    [Fact]
    public async Task AMemberWithoutPrintSendsNothingToThePrinter()
    {
        (Guid uuid, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser viewer, HttpClient viewerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "page-send-viewer@example.com");

        using (ownerClient)
        using (viewerClient)
        {
            await JoinAsync(viewer.Id, await TeamOfAsync(uuid), CapabilityPresets.Viewer);
            await UploadAsync(viewerClient, "hopeful.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(viewerClient, "Send", "hopeful.gcode", uuid);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");
            (await StatusShownAsync(viewerClient, uuid)).Should().Contain("permission");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// Posts one of the page's per-file dispatch forms the way the rendered form does: route values
    /// in the query string, the antiforgery token in the body.
    /// </summary>
    private static async Task<HttpResponseMessage> PostHandlerAsync(HttpClient client, string handler, string name, Guid uuid)
    {
        string page = await GetPageAsync(client, uuid);

        using FormUrlEncodedContent body = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        ]);

        return await client.PostAsync(
            $"/Files?handler={handler}&name={Uri.EscapeDataString(name)}&printerUuid={uuid}",
            body, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetPageAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response =
            await client.GetAsync($"/Files?printerUuid={uuid}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The status message the redirect carried, read off the page it lands on.</summary>
    private static async Task<string> StatusShownAsync(HttpClient client, Guid uuid)
    {
        Match match = Regex.Match(await GetPageAsync(client, uuid),
                                  """alert (?:alert-success|alert-warning)" role="alert">([^<]*)<""");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StringContent body = new(FileContent);

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the upload is setup for this test, not what it verifies");
    }

    /// <summary>A printer on the user's own default team, inserted directly - enrolment is another
    /// file's subject.</summary>
    private async Task<Guid> SeedPrinterAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == userId && member.IsDefault,
                                                          TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = membership.TeamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer.Uuid;
    }

    private async Task<int> TeamOfAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Printers
                            .Where(printer => printer.Uuid == uuid)
                            .Select(printer => printer.TeamId)
                            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task JoinAsync(long userId, int teamId, IReadOnlyList<Capability> capabilities)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Capabilities = CapabilitySet.Format(capabilities),
            IsDefault = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string[]> QueuedFileNamesAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.QueuedPrints
                            .OrderBy(queued => queued.Position)
                            .Select(queued => queued.PrintFile!.Name)
                            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An enrolled, connected printer whose owner is signed in - the send tests need a live socket,
    /// both to receive the transfer and to prove nothing was sent.
    /// </summary>
    private async Task<(Guid uuid, HttpClient client, FakePrinterClient fake, Task run)> ConnectedPrinterAsync()
    {
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterClient fake = new(identity, TimeProvider.System) { Token = token };
        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        await FakePrinterConnections.WaitUntilConnectedAsync(_factory, printerId);

        Guid uuid;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            uuid = (await context.Printers.SingleAsync(printer => printer.Id == printerId,
                                                       TestContext.Current.CancellationToken)).Uuid;
        }

        HSUser owner = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);

        return (uuid, await EnrolmentFlowHelper.SignInAsAsync(_factory, owner), fake, run);
    }

    private static async Task<FakeTransfer> WaitForTransferAsync(FakePrinterClient fake)
    {
        bool ended = await FakePrinterConnections.WaitUntilAsync(
            () => fake.Device.LastTransfer is not null, TimeSpan.FromSeconds(30));

        ended.Should().BeTrue("the transfer never reached a terminal state");
        fake.ReplyFault.Should().BeNull();

        return fake.Device.LastTransfer!;
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fake.ReplyFault.Should().BeNull("a faulted fake would invalidate what this test claims about the server");

        await fake.DisposeAsync();
    }
}
