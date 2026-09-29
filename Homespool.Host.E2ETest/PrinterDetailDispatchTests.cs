using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// The printer page's queue and print controls - Move, Cancel and the print intents - through the
/// real form posts, each once as somebody permitted and once as somebody whose membership does not
/// grant the act.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refused posts are made without the buttons.</b> The page hides a control from anybody
/// whose capabilities its service would refuse, so a Contributor posting Move or Pause is the "a
/// button that is not rendered is not a permission check" case the page's own remarks name, driven
/// from outside. What each preset is offered is asserted separately, on the rendered page.
/// </para>
/// <para>
/// <b>The posts are asserted on the database and the printer rather than on the page.</b> The queue
/// order is a stored fact and a pause is a device state; what the page says about either is
/// presentation.
/// </para>
/// </remarks>
public sealed class PrinterDetailDispatchTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("detail-dispatch");
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
    /// Moving an entry through the page reorders the stored queue - the case the refusal below must
    /// not swallow.
    /// </summary>
    [Fact]
    public async Task MovingThroughThePageReordersTheQueue()
    {
        (HSUser owner, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-mover@example.com");

        using (client)
        {
            Guid uuid = await SeedPrinterAsync(owner.Id);
            await UploadAsync(client, "first.gcode");
            await UploadAsync(client, "second.gcode");
            await EnqueueAsync(client, uuid, "first.gcode");
            Guid second = await EnqueueAsync(client, uuid, "second.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "Move",
            [
                new("printUuid", second.ToString()),
                new("position", "0"),
            ]);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedFileNamesAsync()).Should().Equal(["second.gcode", "first.gcode"],
                                                          "the move must reach the stored order");
        }
    }

    /// <summary>
    /// A member who may print but not run the machine cannot reorder the queue: there is one queue,
    /// so moving their entry moves everybody's.
    /// </summary>
    [Fact]
    public async Task AMemberWithoutControlPrinterCannotMoveTheQueue()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-move-owner@example.com");
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-move-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            Guid uuid = await SeedPrinterAsync(owner.Id);
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            await UploadAsync(ownerClient, "first.gcode");
            await UploadAsync(contributorClient, "mine.gcode");
            await EnqueueAsync(ownerClient, uuid, "first.gcode");
            Guid theirOwn = await EnqueueAsync(contributorClient, uuid, "mine.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Move",
            [
                new("printUuid", theirOwn.ToString()),
                new("position", "0"),
            ]);

            AssertAccessDenied(posted);

            (await QueuedFileNamesAsync()).Should().Equal(["first.gcode", "mine.gcode"],
                                                          "a refused move leaves the order it found");
        }
    }

    /// <summary>
    /// A member holding <c>Print</c> cancels their own entry - withdrawal of your own work is what
    /// the capability covers.
    /// </summary>
    [Fact]
    public async Task AMemberWithPrintCancelsTheirOwnEntry()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-cancel-owner@example.com");
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-cancel-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            Guid uuid = await SeedPrinterAsync(owner.Id);
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            await UploadAsync(contributorClient, "mine.gcode");
            Guid theirOwn = await EnqueueAsync(contributorClient, uuid, "mine.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Cancel",
            [
                new("printUuid", theirOwn.ToString()),
            ]);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedFileNamesAsync()).Should().BeEmpty("cancelling your own entry is what Print permits");
        }
    }

    /// <summary>
    /// The same member cannot cancel somebody else's entry: withdrawing another person's work is
    /// <c>ControlPrinter</c>'s business.
    /// </summary>
    [Fact]
    public async Task AMemberWithPrintCannotCancelAnothersEntry()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-keep-owner@example.com");
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-keep-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            Guid uuid = await SeedPrinterAsync(owner.Id);
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            await UploadAsync(ownerClient, "owners.gcode");
            Guid theirs = await EnqueueAsync(ownerClient, uuid, "owners.gcode");

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Cancel",
            [
                new("printUuid", theirs.ToString()),
            ]);

            AssertAccessDenied(posted);

            (await QueuedFileNamesAsync()).Should().Equal(["owners.gcode"],
                                                          "somebody else's work must survive the attempt");
        }
    }

    /// <summary>
    /// Pausing through the page reaches the printer and the printer actually pauses - the intent
    /// path the queue buttons do not cross.
    /// </summary>
    [Fact]
    public async Task PausingThroughThePageReachesThePrinter()
    {
        (Guid uuid, long _, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "Pause", []);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            fake.Device.State.Should().Be(DeviceState.Paused,
                                          "the command must have executed, not merely been accepted by the page");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A member who may print cannot pause: the running print may be somebody else's, and steering
    /// the machine is <c>ControlPrinter</c>'s business. Nothing reaches the printer.
    /// </summary>
    /// <remarks>
    /// The printer is connected and printing on purpose: refused earlier, for not being connected,
    /// this test would pass against a codebase with the permission gate deleted.
    /// </remarks>
    [Fact]
    public async Task AMemberWithoutControlPrinterCannotPause()
    {
        (Guid uuid, long _, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-pause-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Pause", []);

            AssertAccessDenied(posted);

            fake.Device.State.Should().Be(DeviceState.Printing, "the refusal must come before the frame");
            fake.ReceivedCommands.Should().BeEmpty();

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// Resuming through the page reaches the printer and the print runs again.
    /// </summary>
    [Fact]
    public async Task ResumingThroughThePageReachesThePrinter()
    {
        (Guid uuid, long _, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");
            fake.Device.TryPause().Should().BeTrue("the print has to be paused for a resume to mean anything");

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "Resume", []);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            fake.Device.State.Should().Be(DeviceState.Printing,
                                          "the command must have executed, not merely been accepted by the page");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A member who may print cannot resume, any more than pause: the paused print may be somebody
    /// else's, and when it restarts is <c>ControlPrinter</c>'s business. Nothing reaches the printer.
    /// </summary>
    /// <remarks>
    /// <c>ResumePrint</c> declares no capability of its own and takes the intent default, so this is
    /// the test that notices if it ever declares a lower one.
    /// </remarks>
    [Fact]
    public async Task AMemberWithoutControlPrinterCannotResume()
    {
        (Guid uuid, long _, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-resume-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");
            fake.Device.TryPause().Should().BeTrue();

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Resume", []);

            AssertAccessDenied(posted);

            fake.Device.State.Should().Be(DeviceState.Paused, "the refusal must come before the frame");
            fake.ReceivedCommands.Should().BeEmpty();

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// Stopping through the page stops the printer and records who asked, on the print's own row.
    /// </summary>
    /// <remarks>
    /// <b>The record is the half worth asserting.</b> A stop sent from here and one pressed at the
    /// panel are the same state change on the wire, so the row is the only place the difference
    /// survives - and the page's handler is a different door from the API's onto the same service.
    /// </remarks>
    [Fact]
    public async Task StoppingThroughThePageStopsThePrinterAndRecordsWho()
    {
        (Guid uuid, long ownerId, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            fake.Device.StartPrint(jobId: 7, path: "/usb/PLATE.GCO");
            await SeedOpenPrintAsync(uuid, ownerId);

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "Stop", []);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            fake.Device.State.Should().Be(DeviceState.Stopped, "the command must have executed");
            (await StoppedByAsync(uuid)).Should().Be(ownerId);

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// <b>A member who may print stops their own print</b> - withdrawing your own work is what
    /// <c>Print</c> covers, and it is the one machine control a Contributor holds.
    /// </summary>
    [Fact]
    public async Task AMemberWithPrintStopsTheirOwnPrint()
    {
        (Guid uuid, long _, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-stop-own@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            fake.Device.StartPrint(jobId: 7, path: "/usb/PLATE.GCO");
            await SeedOpenPrintAsync(uuid, contributor.Id);

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Stop", []);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            posted.Headers.Location!.OriginalString.Should().NotContain("/Account/AccessDenied");

            fake.Device.State.Should().Be(DeviceState.Stopped);
            (await StoppedByAsync(uuid)).Should().Be(contributor.Id);

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The same member cannot stop somebody else's print, and nothing reaches the printer.
    /// </summary>
    /// <remarks>
    /// <c>StopPrint</c>'s own floor is <c>Print</c>, which a Contributor holds, so the command layer
    /// would send it. Whose print it is gets decided only by the stop service, which makes this the
    /// test that fails if the page ever sends the stop around it.
    /// </remarks>
    [Fact]
    public async Task AMemberWithPrintCannotStopSomebodyElsesPrint()
    {
        (Guid uuid, long ownerId, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-stop-theirs@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            fake.Device.StartPrint(jobId: 7, path: "/usb/PLATE.GCO");
            await SeedOpenPrintAsync(uuid, ownerId);

            using HttpResponseMessage posted = await PostHandlerAsync(contributorClient, uuid, "Stop", []);

            AssertAccessDenied(posted);

            fake.Device.State.Should().Be(DeviceState.Printing, "the refusal must come before the frame");
            fake.ReceivedCommands.Should().BeEmpty();
            (await StoppedByAsync(uuid)).Should().BeNull("a refused stop is nobody's stop");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A Contributor holds <c>Print</c> and not <c>ControlPrinter</c>, so the page offers them only
    /// what the first permits: removing their own queue entry, and none of the machine controls.
    /// </summary>
    /// <remarks>
    /// The printer is connected, has a hotend and reports PLA loaded, so each absent control is
    /// absent for the capability and not because its other condition failed - the Operator test
    /// below is the proof those conditions hold.
    /// </remarks>
    [Fact]
    public async Task AContributorIsOfferedOnlyWhatPrintPermits()
    {
        (Guid uuid, long _, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ReportingPrinterAsync();
        (HSUser contributor, HttpClient contributorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-offer-contributor@example.com");

        using (ownerClient)
        using (contributorClient)
        {
            await JoinAsync(contributor.Id, await TeamOfAsync(uuid), CapabilityPresets.Contributor);

            await UploadAsync(ownerClient, "owners.gcode");
            await UploadAsync(contributorClient, "mine.gcode");
            Guid theirs = await EnqueueAsync(ownerClient, uuid, "owners.gcode");
            Guid theirOwn = await EnqueueAsync(contributorClient, uuid, "mine.gcode");

            string html = await GetPageAsync(contributorClient, uuid);

            foreach (string handler in (string[])["Pause", "Resume", "Stop", "Preheat", "Cooldown", "Unload", "Move"])
            {
                html.Should().NotContain($"handler={handler}\"", $"{handler} needs ControlPrinter, which a Contributor lacks");
            }

            html.Should().Contain($"value=\"{theirOwn}\"", "removing your own entry is what Print permits");
            html.Should().NotContain($"value=\"{theirs}\"", "removing somebody else's entry needs ControlPrinter");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// An Operator holds <c>ControlPrinter</c>, so the page offers the machine controls and removing
    /// anybody's queue entry.
    /// </summary>
    [Fact]
    public async Task AnOperatorIsOfferedTheMachineControls()
    {
        (Guid uuid, long _, HttpClient ownerClient, FakePrinterClient fake, Task run) = await ReportingPrinterAsync();
        (HSUser @operator, HttpClient operatorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-offer-operator@example.com");

        using (ownerClient)
        using (operatorClient)
        {
            await JoinAsync(@operator.Id, await TeamOfAsync(uuid), CapabilityPresets.Operator);

            await UploadAsync(ownerClient, "owners.gcode");
            Guid theirs = await EnqueueAsync(ownerClient, uuid, "owners.gcode");

            string html = await GetPageAsync(operatorClient, uuid);

            foreach (string handler in (string[])["Pause", "Resume", "Stop", "Preheat", "Cooldown", "Unload", "Move", "Cancel"])
            {
                html.Should().Contain($"handler={handler}\"", $"an Operator may {handler}");
            }

            html.Should().Contain($"value=\"{theirs}\"", "ControlPrinter removes anybody's entry");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// Unloading through the page empties the tool on the printer, the printer reports it, and the
    /// page stops offering to unload filament that is no longer there.
    /// </summary>
    /// <remarks>
    /// The fake's idle interval is a minute, far past the wait here, so the report arriving at all
    /// proves it was sent because the material changed - the way firmware sends telemetry - and not
    /// on the timer.
    /// </remarks>
    [Fact]
    public async Task UnloadingThroughThePageIsReportedAndTheButtonGoes()
    {
        SyntheticTelemetrySource source = new() { IdleInterval = TimeSpan.FromMinutes(1) };
        (Guid uuid, long _, HttpClient client, FakePrinterClient fake, Task run) =
            await ReportingPrinterAsync(new FakePrinterOptions { TelemetrySource = source });

        using (client)
        {
            (await GetPageAsync(client, uuid)).Should().Contain("handler=Unload\"", "PLA is loaded and reported");

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "Unload", [new("tool", "1")]);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await WaitForMaterialAsync(uuid, expected: null)).Should().BeTrue(
                "the emptied tool must reach the server well inside the fake's minute-long idle interval");

            fake.Device.MaterialOf(1).Should().BeNull();
            Encoding.ASCII.GetString(fake.ReceivedCommands.Single().Payload.Span).Should().Be("M702 T0 W0");
            (await GetPageAsync(client, uuid)).Should().NotContain("handler=Unload\"", "there is nothing left to unload");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The whole cancel-object path: the printer declares its objects, the plate names them from the
    /// file's own header, and the owner's cancel reaches the printer and comes back as the new state.
    /// </summary>
    /// <remarks>
    /// Named objects rather than numbers, so the fragment containing them proves the header was
    /// asked of the printer and read - the list by number would render without it.
    /// </remarks>
    [Fact]
    public async Task TheOwnerCancellingAnObjectReachesThePrinterAndComesBack()
    {
        (Guid uuid, long ownerId, HttpClient client, FakePrinterClient fake, Task run) = await PrintingPlateAsync();

        using (client)
        {
            await SeedOpenPrintAsync(uuid, ownerId);

            string plate = await GetPlateAsync(client, uuid, until: html => html.Contains("Hinge", StringComparison.Ordinal));

            plate.Should().Contain("Hinge", "the names come from the file's objects_info, asked of the printer");
            plate.Should().Contain("handler=CancelObject\"", "the print is the reader's own");

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "CancelObject", [new("objectId", "1")]);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            posted.Headers.Location!.OriginalString.Should().NotContain("AccessDenied");

            fake.Device.CancelledObjects.Should().Equal([1], "id 1 is the second object, Hinge - the id posted, not the number shown");

            fake.ReceivedCommands.Select(frame => Encoding.UTF8.GetString(frame.Payload.Span))
                .Should().Contain("""{"command":"CANCEL_OBJECT","args":[],"kwargs":{"id":1}}""");

            (await WaitForLiveStateAsync(uuid, live => live.CancelledObjectIds == "1")).Should().BeTrue(
                "the printer's answer carries the new set, and it is what the page reads");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// <b>Running the machine does not extend to editing somebody's print.</b> An Operator may stop
    /// anybody's print, and is still refused a cancel on one they did not queue - offered no button,
    /// and refused the post made without one. Nothing reaches the printer.
    /// </summary>
    [Fact]
    public async Task AnOperatorCannotCancelAnObjectOfSomebodyElsesPrint()
    {
        (Guid uuid, long ownerId, HttpClient ownerClient, FakePrinterClient fake, Task run) = await PrintingPlateAsync();
        (HSUser @operator, HttpClient operatorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-cancel-object-operator@example.com");

        using (ownerClient)
        using (operatorClient)
        {
            await JoinAsync(@operator.Id, await TeamOfAsync(uuid), CapabilityPresets.Operator);
            await SeedOpenPrintAsync(uuid, ownerId);

            string plate = await GetPlateAsync(operatorClient, uuid, until: html => html.Contains("Hinge", StringComparison.Ordinal));

            plate.Should().Contain("Hinge", "an Operator still sees the plate");
            plate.Should().NotContain("handler=CancelObject", "the print is not theirs");
            (await GetPageAsync(operatorClient, uuid)).Should().Contain("handler=Stop\"", "stopping it is still theirs to do");

            using HttpResponseMessage posted = await PostHandlerAsync(operatorClient, uuid, "CancelObject", [new("objectId", "1")]);

            AssertAccessDenied(posted);

            fake.Device.CancelledObjects.Should().BeEmpty();
            fake.ReceivedCommands.Select(frame => frame.TryGetJsonCommandName()).Should().NotContain("CANCEL_OBJECT");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The owner cancelling an object the printer has not reported is told so on the page, and the
    /// printer is sent nothing - the server's count, not the printer's refusal, is what says no.
    /// </summary>
    /// <remarks>
    /// Id 4 of four, the first one past the end, so a bound that let the count itself through would
    /// send it. The message numbers it from one, as the plate does.
    /// </remarks>
    [Fact]
    public async Task CancellingAnObjectThePrinterHasNotReportedReachesNoPrinter()
    {
        (Guid uuid, long ownerId, HttpClient client, FakePrinterClient fake, Task run) = await PrintingPlateAsync();

        using (client)
        {
            await SeedOpenPrintAsync(uuid, ownerId);

            using HttpResponseMessage posted = await PostHandlerAsync(client, uuid, "CancelObject", [new("objectId", "4")]);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            posted.Headers.Location!.OriginalString.Should().NotContain("AccessDenied", "the print is the owner's own");

            (await GetPageAsync(client, uuid)).Should().Contain("This print has no object 5 that can be cancelled.");

            fake.Device.CancelledObjects.Should().BeEmpty();
            fake.ReceivedCommands.Select(frame => frame.TryGetJsonCommandName()).Should().NotContain("CANCEL_OBJECT");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// <b>A print of one object gets no plate at all.</b> Cancelling the only object does not stop
    /// the print - firmware skips its moves and runs the rest - so the card would offer a worse Stop
    /// beside the real one. The printer still reports the object; the page declines to show it.
    /// </summary>
    [Fact]
    public async Task ASingleObjectPrintShowsNoPlate()
    {
        (Guid uuid, long ownerId, HttpClient client, FakePrinterClient fake, Task run) = await PrintingPlateAsync(objectCount: 1);

        using (client)
        {
            await SeedOpenPrintAsync(uuid, ownerId);

            using HttpResponseMessage fragment = await client.GetAsync($"/Printers/Detail/{uuid}?handler=Plate",
                                                                       TestContext.Current.CancellationToken);
            string plate = await fragment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            plate.Should().NotContain("handler=CancelObject");
            plate.Should().NotContain("print-plate", "no card is rendered for one object");
            fake.ReceivedCommands.Select(frame => frame.TryGetJsonCommandName())
                .Should().NotContain("SEND_FILE_INFO", "a plate that is not shown is not asked for");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A connected printer running a plate - four objects unless asked for another count - from a
    /// file whose header names them, with the server having heard the objects declared.
    /// </summary>
    private async Task<(Guid uuid, long userId, HttpClient client, FakePrinterClient fake, Task run)> PrintingPlateAsync(
        int objectCount = 4)
    {
        const string ObjectsInfo =
            """{"objects":[{"name":"Lid","polygon":[[20,20],[60,20],[60,60],[20,60]]},{"name":"Hinge","polygon":[[80,20],[120,20],[120,60],[80,60]]},{"name":"Base","polygon":[[20,120],[60,120],[60,160],[20,160]]},{"name":"Clip","polygon":[[80,120],[120,120],[120,160],[80,160]]}]}""";

        SyntheticTelemetrySource source = new()
        {
            IdleInterval = TimeSpan.FromMilliseconds(200),
            PrintingInterval = TimeSpan.FromMilliseconds(200),
        };

        (Guid uuid, long userId, HttpClient client, FakePrinterClient fake, Task run) connected =
            await ConnectedPrinterAsync(new FakePrinterOptions { TelemetrySource = source });

        connected.fake.Device.Storage.AddFile("/usb/PLATE.GCO", size: 1000, modified: 0,
                                              objectsInfo: ObjectsInfo, bedShape: "0x0,250x0,250x210,0x210");
        connected.fake.Device.StartPrint(jobId: 7, path: "/usb/PLATE.GCO");
        connected.fake.Device.DeclareObjects(objectCount);

        (await WaitForLiveStateAsync(connected.uuid, live => live.CancellableObjectCount == objectCount && live.JobId == 7))
            .Should().BeTrue("the fake reports its objects unasked, ahead of its next telemetry");

        return connected;
    }

    /// <summary>
    /// The open print row the queue would have written had it started this print - which is what
    /// makes it somebody's.
    /// </summary>
    private async Task SeedOpenPrintAsync(Guid uuid, long queuedBy)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == uuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        context.PrintJobs.Add(new PrintJob
        {
            PrintUuid = Guid.NewGuid(),
            PrinterId = printerId,
            FileName = "plate.gcode",
            QueuedByUserId = queuedBy,
            PrinterPath = "/usb/PLATE.GCO",
            FirmwareJobId = 7,
            State = PrintState.Printing,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Who the printer's one open print row says stopped it; null is nobody here.</summary>
    private async Task<long?> StoppedByAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == uuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        return await context.PrintJobs
                            .Where(job => job.PrinterId == printerId)
                            .Select(job => job.StoppedByUserId)
                            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The plate fragment, polled until <paramref name="until"/> holds or ten seconds pass - the
    /// first read of a print's names is asked of the printer, which the fragment waits on only
    /// briefly.
    /// </summary>
    private static async Task<string> GetPlateAsync(HttpClient client, Guid uuid, Func<string, bool> until)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string html = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            using HttpResponseMessage fragment = await client.GetAsync($"/Printers/Detail/{uuid}?handler=Plate",
                                                                       TestContext.Current.CancellationToken);

            fragment.StatusCode.Should().Be(HttpStatusCode.OK);

            html = await fragment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            if (until(html))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        return html;
    }

    /// <summary>Waits for the server's live state for this printer to satisfy <paramref name="condition"/>.</summary>
    private async Task<bool> WaitForLiveStateAsync(Guid uuid, Func<PrinterLiveState, bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            PrinterLiveState? live = await context.PrinterLiveStates
                                                  .AsNoTracking()
                                                  .Where(state => context.Printers.Any(printer => printer.Uuid == uuid &&
                                                                                                  printer.Id == state.PrinterId))
                                                  .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            if (live is not null && condition(live))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>
    /// <see cref="ConnectedPrinterAsync"/> with telemetry, returning once the server has heard the
    /// fake's loaded PLA - the page offers Unload only for a material the printer has named.
    /// </summary>
    private async Task<(Guid uuid, long userId, HttpClient client, FakePrinterClient fake, Task run)> ReportingPrinterAsync(
        FakePrinterOptions? options = null)
    {
        (Guid uuid, long userId, HttpClient client, FakePrinterClient fake, Task run) connected =
            await ConnectedPrinterAsync(options ?? new FakePrinterOptions { TelemetrySource = new SyntheticTelemetrySource() });

        (await WaitForMaterialAsync(connected.uuid, FakeDevice.DefaultMaterial)).Should().BeTrue(
            "the first telemetry, sent on connect, names the loaded material");

        return connected;
    }

    /// <summary>Waits for the server's live state to say <paramref name="expected"/> is loaded; null is empty.</summary>
    private async Task<bool> WaitForMaterialAsync(Guid uuid, string? expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            int printerId = await context.Printers
                                         .Where(printer => printer.Uuid == uuid)
                                         .Select(printer => printer.Id)
                                         .SingleAsync(TestContext.Current.CancellationToken);

            PrinterLiveState? live = await context.PrinterLiveStates
                                                  .AsNoTracking()
                                                  .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                                        TestContext.Current.CancellationToken);

            if (live is not null && live.Material == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<string> GetPageAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage page = await client.GetAsync($"/Printers/Detail/{uuid}",
                                                               TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK, "the reader is a member who can see the printer");

        return await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// What a cookie session's refused post looks like: the access-denied redirect, since pages
    /// answer people rather than scripts. The API's 403 twin lives in
    /// <c>PrinterControllerDispatchTests</c>.
    /// </summary>
    private static void AssertAccessDenied(HttpResponseMessage posted)
    {
        posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        posted.Headers.Location!.AbsolutePath.Should().Be("/Account/AccessDenied",
                                                          "a refused post is a refusal, not a success redirect");
    }

    /// <summary>
    /// Posts one of the Detail page's forms the way the rendered form does. The antiforgery token
    /// is scraped from the page, which renders a form for every signed-in reader.
    /// </summary>
    private static async Task<HttpResponseMessage> PostHandlerAsync(HttpClient client,
                                                                    Guid uuid,
                                                                    string handler,
                                                                    IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        using HttpResponseMessage page = await client.GetAsync($"/Printers/Detail/{uuid}",
                                                               TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK, "the poster is a member who can see the printer");

        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        List<KeyValuePair<string, string>> body =
            [new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(html)), .. fields];

        using FormUrlEncodedContent content = new(body);

        return await client.PostAsync($"/Printers/Detail/{uuid}?handler={handler}", content,
                                      TestContext.Current.CancellationToken);
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StringContent body = new("G28 ; home\n");

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the upload is setup for this test, not what it verifies");
    }

    private static async Task<Guid> EnqueueAsync(HttpClient client, Guid uuid, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/queue",
                                                                          new { name }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the entry is setup for this test, not what it verifies");

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("printUuid").GetGuid();
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
    /// An enrolled, connected printer whose owner is signed in - the intent tests need a live
    /// socket, both to pause and to prove nothing was sent.
    /// </summary>
    private async Task<(Guid uuid, long userId, HttpClient client, FakePrinterClient fake, Task run)> ConnectedPrinterAsync(
        FakePrinterOptions? options = null)
    {
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterClient fake = new(identity, TimeProvider.System, options) { Token = token };
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

        return (uuid, userId, await EnrolmentFlowHelper.SignInAsAsync(_factory, owner), fake, run);
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fake.ReplyFault.Should().BeNull("a faulted fake would invalidate what this test claims about the server");

        await fake.DisposeAsync();
    }
}
