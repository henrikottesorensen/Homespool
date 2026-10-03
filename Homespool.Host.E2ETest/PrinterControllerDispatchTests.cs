using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>PrinterController</c>'s dispatch endpoints - send a file, browse storage - each once
/// with a token whose scope names the capability and once with one that does not, against a
/// genuinely connected printer; their refusals that are not about permission, and the answers a
/// listing cannot be made from; the job-control verbs nothing else drives over the API; and the
/// readying route that is switched off.
/// </summary>
/// <remarks>
/// <para>
/// <b>The negative half asserts on the printer as well as the status code.</b> A 403 is cheap to
/// produce for the wrong reason; a connected fake that received nothing is what proves the refusal
/// came before the frame.
/// </para>
/// <para>
/// <b>A scope refusal names the missing capability</b>, which is what sends the caller to mint a
/// replacement token rather than to ask their team for access - the two kinds of 403 the
/// documentation tells apart.
/// </para>
/// </remarks>
public sealed class PrinterControllerDispatchTests : IAsyncLifetime
{
    private const string FileContent = "G28 ; home\n";

    /// <summary>A CORE One's <c>printer_type</c> - built with the LED strips, and reporting them.</summary>
    private const string CoreOne = "7.1.0";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("controller-dispatch");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        // So that a POST to a path with no route answers 404, as the published application does.
        // Unpublished output gets MapStaticAssets' GET-and-HEAD fallback on {**path:file}, which the
        // matcher counts as a candidate for every path - so any other verb to an unrouted path would
        // answer 405 here and nowhere else.
        _factory.ConfigurationOverrides["ReloadStaticAssetsAtRuntime"] = "false";

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
    /// A token scoped to <c>Print</c> sends a file, and the bytes arrive intact - the case the
    /// refusals below must not swallow.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToPrintSendsAFile()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        await UploadAsOwnerAsync(userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "204 means the printer accepted the transfer");

        FakeTransfer transfer = await WaitForTransferAsync(fake);

        transfer.IsComplete.Should().BeTrue();
        transfer.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes(FileContent),
                                                  "what the printer received must be what was uploaded");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>Print</c> is refused with a 403 naming it, and nothing
    /// reaches the printer.
    /// </summary>
    /// <remarks>
    /// The file exists and the printer is visible to the token - <c>Print</c> is the only thing the
    /// scope is missing, so the refusal can only be the scope's.
    /// </remarks>
    [Fact]
    public async Task ATokenWithoutPrintCannotSendAFileAndTheRefusalNamesIt()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        await UploadAsOwnerAsync(userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ViewPrinter]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("Print",
                                                         "a scope refusal names the capability, so the fix is a new token");

        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Without <c>Print</c>, a name that exists and one that does not get the same refusal - so the
    /// endpoint cannot be used to find out which files somebody has.
    /// </summary>
    /// <remarks>
    /// The token does not name <c>ViewOwnFiles</c> either, so the file list is closed to it; if the
    /// send looked the file up before asking <c>Print</c>, "no such file" would answer what listing
    /// may not.
    /// </remarks>
    [Fact]
    public async Task ATokenWithoutPrintCannotTellWhichFilesExist()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        await UploadAsOwnerAsync(userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ViewPrinter]);

        using HttpResponseMessage existing = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);
        using HttpResponseMessage absent = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                        new { name = "nothing-here.gcode" },
                                                                        TestContext.Current.CancellationToken);

        absent.StatusCode.Should().Be(existing.StatusCode);
        absent.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(absent)).Should().Be(await DetailOfAsync(existing),
                                                  "the refusal must not depend on whether the name exists");

        fake.ReceivedCommands.Should().BeEmpty();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Sending refuses a file the caller does not have, with the same answer it gives for one that
    /// does not exist - and the printer hears nothing. This is the ownership check on the send path,
    /// which the store cannot make on its own because it never sees who is asking.
    /// </summary>
    /// <remarks>
    /// Against a printer the caller may print to, so the 404 can only be the file's: an unknown
    /// printer is refused first, and would pass this for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task SendingAFileYouDoNotOwnIsNotFound()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser _, HttpClient other) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "controller-send-other@example.com");

        using (other)
        {
            await UploadAsync(other, "theirs.gcode");
        }

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage theirs = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                        new { name = "theirs.gcode" },
                                                                        TestContext.Current.CancellationToken);
        using HttpResponseMessage nobodys = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                         new { name = "nobodys.gcode" },
                                                                         TestContext.Current.CancellationToken);

        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound);
        nobodys.StatusCode.Should().Be(HttpStatusCode.NotFound);

        fake.ReceivedCommands.Should().BeEmpty("a file the caller does not own must not reach the printer");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A member whose team grants only viewing gets the other kind of 403 - and nothing reaches the
    /// printer either.
    /// </summary>
    /// <remarks>
    /// The credential is unrestricted, so this is the membership refusing where the test above has
    /// the scope refuse. The documentation tells the reader apart by whether a capability is named;
    /// what this asserts is the half that must hold whichever kind it is.
    /// </remarks>
    [Fact]
    public async Task AMemberWhoMayOnlyViewCannotSendAFile()
    {
        (Guid uuid, long _, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser viewer, HttpClient viewerCookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "controller-send-viewer@example.com");

        using (viewerCookieClient)
        {
            await JoinAsync(viewer.Id, await TeamOfAsync(uuid), CapabilityPresets.Viewer);
            await UploadAsync(viewerCookieClient, "hopeful.gcode");
        }

        using HttpClient client = await ScopedClientAsync(viewer.Id, CapabilitySet.Everything);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "hopeful.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        fake.ReceivedCommands.Should().BeEmpty("a Viewer membership must not reach the printer");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A file that is found and then cannot be opened is a 409 naming it, and no command leaves -
    /// a download offered under a token with no bytes behind it would send the printer after nothing.
    /// </summary>
    /// <remarks>
    /// Against a connected printer, because the connection is asked before the file is opened: a
    /// disconnected one would answer 409 first, for a reason this test is not about.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableFileIsAConflictAndReachesNoPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        await UploadAsOwnerAsync(userId, "benchy.gcode");
        UnreadableStoredFile.Make(_factory, userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DetailOfAsync(response)).Should().Be("benchy.gcode could not be read - it may have just been deleted.");

        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A file of 4 GiB or more is a 400 saying so, and no command leaves: firmware is told a file's
    /// size as a 32-bit number, so the printer could only refuse it.
    /// </summary>
    [Fact]
    public async Task AFileTooLargeForAPrinterIsABadRequestAndReachesNoPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        await UploadAsOwnerAsync(userId, "benchy.gcode");
        OversizedStoredFile.Make(_factory, userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailOfAsync(response)).Should().Be("Files must be under 4 GiB - a printer cannot be sent anything larger.");

        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Nothing over the API starts a print directly - not a slicer's key, and not an unrestricted
    /// one - and the printer hears nothing.
    /// </summary>
    /// <remarks>
    /// <b>The printer is idle and holds the file</b>, which is everything firmware needs to accept
    /// <c>START_PRINT</c>. So an idle printer afterwards is the route's absence rather than the
    /// machine refusing, and the unrestricted token makes it the route's absence rather than a scope.
    /// Printing goes through the queue, which waits for a person to ready the printer.
    /// </remarks>
    [Fact]
    public async Task NoTokenStartsAPrintDirectly()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/model.gcode", 4242, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, CapabilitySet.Everything);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/print",
                                                                          new { path = "/usb/model.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no such route, rather than one that refuses");

        fake.Device.State.Should().Be(DeviceState.Idle);
        fake.ReceivedCommands.Should().BeEmpty("nothing may reach the printer that could start it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>ControlPrinter</c> browses the printer's storage - the endpoint's own
    /// gate, above the weaker one the wire command declares for the queue loop's sake.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToControlPrinterBrowsesStorage()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/lampshade.gcode", 7647560, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        payload.RootElement.GetProperty("entries").EnumerateArray()
               .Single().GetProperty("name").GetString().Should().Be("lampshade.gcode");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>ControlPrinter</c> is refused the listing with a 403
    /// naming it, and the printer is asked nothing - browsing reads, but by making the machine go and
    /// work.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutControlPrinterCannotBrowseStorageAndTheRefusalNamesIt()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/lampshade.gcode", 7647560, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ViewPrinter]);

        using HttpResponseMessage response =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("ControlPrinter",
                                                         "a scope refusal names the capability, so the fix is a new token");

        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the command, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A member whose team grants only viewing is refused the listing with the other kind of 403 -
    /// one naming no capability - and the printer is asked nothing.
    /// </summary>
    /// <remarks>
    /// The credential is unrestricted, so this is the membership refusing where the test above has
    /// the scope refuse. The documentation tells the two apart by whether a capability is named, and
    /// a team refusal naming one would send the reader to mint a token that cannot help.
    /// </remarks>
    [Fact]
    public async Task AMemberWhoMayOnlyViewCannotBrowseStorage()
    {
        (Guid uuid, long _, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/lampshade.gcode", 7647560, 1764804970));
        (HSUser viewer, HttpClient viewerCookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "controller-browse-viewer@example.com");

        using (viewerCookieClient)
        {
            await JoinAsync(viewer.Id, await TeamOfAsync(uuid), CapabilityPresets.Viewer);
        }

        using HttpClient client = await ScopedClientAsync(viewer.Id, CapabilitySet.Everything);

        using HttpResponseMessage response =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().NotContain("ControlPrinter",
                                                            "a team refusal names nothing, because no token would fix it");

        fake.ReceivedCommands.Should().BeEmpty("a Viewer membership must not reach the printer");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A path climbing out of a directory is refused here, and the printer is never asked - firmware
    /// would refuse it too, but only after a command had been spent on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kestrel delivers this path from an absolute-form request target</b> -
    /// <c>POST http://host/api/v1/printers/{uuid}/storage/usb/..%2Fsecret</c> - because it takes that
    /// form's path from <c>Uri.LocalPath</c>, which decodes the slash after dot segments have been
    /// resolved. An origin-form target never gets here: Kestrel removes its dot segments and leaves
    /// an encoded slash encoded.
    /// </para>
    /// <para>
    /// <b><c>HttpClient</c> cannot send it either</b>, for the same two reasons, so the path is set on
    /// the request directly - as Kestrel would hand it over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATraversalIsRefusedBeforeThePrinterIsAsked()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        string token = await MintTokenAsync(userId, [Capability.ControlPrinter]);

        HttpContext answered = await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/api/v1/printers/{uuid}/storage/usb/../secret";
            context.Request.Headers.Authorization = $"Bearer {token}";
        }, TestContext.Current.CancellationToken);

        answered.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        fake.ReceivedCommands.Should().BeEmpty("a traversal must not cost the printer a command");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A path climbing out with a backslash is refused too - the printer's filesystem takes
    /// <c>\</c> as a separator, and its own check looks only for <c>/../</c>.
    /// </summary>
    /// <remarks>
    /// Unlike the slash, this needs no unusual request: Kestrel decodes <c>%5C</c> and removes dot
    /// segments only between slashes, so an ordinary <see cref="HttpClient"/> request delivers it.
    /// </remarks>
    /// <param name="path">The path after <c>storage/usb/</c>, as the client writes it.</param>
    [Theory]
    [InlineData("..%5Csecret")]
    [InlineData("sub%5C..%5C..%5Csecret")]
    [InlineData("sub/..%5Csecret")]
    [InlineData("sub%5C..")]
    public async Task ABackslashTraversalIsRefusedBeforeThePrinterIsAsked(string path)
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/printers/{uuid}/storage/usb/{path}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailOfAsync(response)).Should().Contain("'..'");

        fake.ReceivedCommands.Should().BeEmpty("a traversal must not cost the printer a command");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Another team's printer is not found by either endpoint - the same answer as a printer that
    /// does not exist - and hears nothing.
    /// </summary>
    /// <remarks>
    /// The caller's token is unrestricted and their file exists, so the printer being invisible to
    /// them is the only thing left to refuse.
    /// </remarks>
    [Fact]
    public async Task APrinterYouCannotSeeIsNotFoundToSendAndToBrowse()
    {
        (Guid uuid, long _, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser stranger, HttpClient strangerCookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "controller-stranger@example.com");

        using (strangerCookieClient)
        {
            await UploadAsync(strangerCookieClient, "benchy.gcode");
        }

        using HttpClient client = await ScopedClientAsync(stranger.Id, CapabilitySet.Everything);

        Guid[] targets = [uuid, Guid.NewGuid()];

        foreach (Guid target in targets)
        {
            using HttpResponseMessage send = await client.PostAsJsonAsync($"/api/v1/printers/{target}/files",
                                                                          new { name = "benchy.gcode" },
                                                                          TestContext.Current.CancellationToken);
            using HttpResponseMessage browse =
                await client.PostAsync($"/api/v1/printers/{target}/storage/usb", null, TestContext.Current.CancellationToken);

            send.StatusCode.Should().Be(HttpStatusCode.NotFound);
            browse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        fake.ReceivedCommands.Should().BeEmpty("a printer the caller cannot see must not hear from them");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A printer that is enrolled and not connected is a 409 from both endpoints, each naming the act
    /// it could not perform.
    /// </summary>
    [Fact]
    public async Task ADisconnectedPrinterIsAConflictToSendAndToBrowse()
    {
        (PrinterIdentity _, string _, int printerId, long userId) = await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);
        Guid uuid = await UuidOfAsync(printerId);

        await UploadAsOwnerAsync(userId, "benchy.gcode");

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print, Capability.ControlPrinter]);

        using HttpResponseMessage send = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                      new { name = "benchy.gcode" },
                                                                      TestContext.Current.CancellationToken);
        using HttpResponseMessage browse =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        send.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CommandOfAsync(send)).Should().Be("send");

        browse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CommandOfAsync(browse)).Should().Be("browse");
    }

    /// <summary>
    /// A printer that accepts the listing command and answers with no listing in it is a 502 naming
    /// the act - not an empty listing, which would claim the storage is empty when the printer said
    /// nothing about it.
    /// </summary>
    /// <remarks>
    /// The answer is a <c>FINISHED</c> correlated to the command, which is success on the wire, so
    /// this cannot be told from the ordinary case by the event type alone - the payload is what is
    /// missing.
    /// </remarks>
    [Fact]
    public async Task AListingThePrinterAnswersWithoutIsABadGateway()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            policyFactory: identity => new FileInfoAnsweredWith(
                new FirmwareFaithfulPolicy(identity, TimeProvider.System),
                (frame, device) => EventMessageBuilder.Build("FINISHED", device.WireState, frame.CommandId)));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway, "the printer answered, and the answer is unusable");
        (await DetailOfAsync(response)).Should().Be("The printer answered without a listing.");
        (await CommandOfAsync(response)).Should().Be("browse");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A listing whose payload will not parse is a 502 naming the act - the printer's answer arrived
    /// and could not be read, which is the gateway's failure rather than the caller's or the machine's.
    /// </summary>
    /// <remarks>
    /// The payload is a JSON string where an object is expected. The connection survives it: the
    /// answer's <c>data</c> is carried raw to the one reader that parses it, so nothing earlier on the
    /// path has an opinion about its shape.
    /// </remarks>
    [Fact]
    public async Task AListingThePrinterAnswersUnreadablyIsABadGateway()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            policyFactory: identity => new FileInfoAnsweredWith(
                new FirmwareFaithfulPolicy(identity, TimeProvider.System),
                (frame, device) => Encoding.UTF8.GetBytes(
                    $$"""{"data":"not a listing","state":"{{device.WireState}}","command_id":{{frame.CommandId}},"event":"FILE_INFO"}""")));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response =
            await client.PostAsync($"/api/v1/printers/{uuid}/storage/usb", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway, "the printer answered, and the answer could not be read");
        (await DetailOfAsync(response)).Should().Match("Printer * answered SEND_FILE_INFO with a payload that could not be read.");
        (await CommandOfAsync(response)).Should().Be("browse");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>ControlPrinter</c> resumes a paused print, and the printer is printing
    /// again afterwards.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToControlPrinterResumesAPausedPrint()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(configure: f =>
        {
            f.Device.StartPrint(jobId: 7);
            f.Device.TryPause();
        });

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/resume",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.Device.State.Should().Be(DeviceState.Printing, "a 204 means the printer resumed, not that we asked");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A resume with nothing paused comes back as the printer's refusal, in its own words, naming the
    /// verb the caller used - the route's word, not the intent's type or the wire's.
    /// </summary>
    [Fact]
    public async Task ResumingWithNothingPausedIsRefusedUnderTheRoutesVerb()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/resume",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DetailOfAsync(response)).Should().Be("No paused print to resume", "the printer's own words");
        (await CommandOfAsync(response)).Should().Be("resume");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>Print</c> alone withdraws a ready printer's readiness - the capability
    /// that entitles somebody to ready it.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToPrintUnreadiesAReadyPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.ForceState(DeviceState.Ready));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/unready",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.Device.State.Should().Be(DeviceState.Idle, "a 204 means the printer stood down, not that we asked");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>ControlPrinter</c> returns a finished printer to idle - leaving the
    /// finished screen, which is the one moment firmware accepts it.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToControlPrinterIdlesAFinishedPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.ForceState(DeviceState.Finished));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/idle",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.Device.State.Should().Be(DeviceState.Idle, "a 204 means the printer left the finished screen, not that we asked");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Idling a printer that is not on the finished or stopped screen comes back as the printer's
    /// refusal, in its own words, under the route's verb - the one route here that is ours rather
    /// than Connect's, so the verb is the invented name and not a wire word.
    /// </summary>
    [Fact]
    public async Task IdlingAPrinterThatIsNotFinishedIsRefusedUnderTheRoutesVerb()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/idle",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DetailOfAsync(response)).Should().Be("Can't set idle now", "the printer's own words");
        (await CommandOfAsync(response)).Should().Be("idle");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>ControlPrinter</c> is refused the idle with a 403 naming
    /// it, and the printer stays on its finished screen having heard nothing.
    /// </summary>
    /// <remarks>
    /// <c>Print</c> is the scope, deliberately: it is what readies and un-readies a printer, and a
    /// reader could take idling for the same family. It is a machine-state act, and gated as one.
    /// </remarks>
    [Fact]
    public async Task ATokenWithoutControlPrinterCannotIdleAPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.ForceState(DeviceState.Finished));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/idle",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("ControlPrinter",
                                                         "a scope refusal names the capability, so the fix is a new token");

        fake.Device.State.Should().Be(DeviceState.Finished);
        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>Print</c> cannot stop a teammate's print, and the refusal names
    /// <c>ControlPrinter</c> - the owner's team grants it, so a replacement token is the fix, and the
    /// team's refusal would send them to ask for access they already have.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutControlPrinterCannotStopATeammatesPrintAndTheRefusalNamesIt()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.StartPrint(jobId: 7));
        (HSUser teammate, HttpClient teammateCookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "controller-stop-teammate@example.com");
        teammateCookieClient.Dispose();

        await JoinAsync(teammate.Id, await TeamOfAsync(uuid), CapabilityPresets.Operator);
        await OpenPrintAsync(uuid, queuedBy: teammate.Id);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/stop",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("ControlPrinter",
                                                         "the team grants it and the key does not, so the fix is a new token");

        fake.Device.State.Should().Be(DeviceState.Printing);
        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the command, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// Nothing over the API readies a printer - the route is absent, not refusing - and a finished
    /// printer that would accept the command stays finished having heard nothing.
    /// </summary>
    /// <remarks>
    /// Readying is a person's assertion that the print sheet is clear, and the API has nobody on
    /// the other end - so <c>Ready</c> is switched off with <c>[NonAction]</c> rather than deleted.
    /// A 404 is what pins that: a live route would answer 204 here, or 403 if the printer's own
    /// remote-ready toggle were off, and either would be the route existing. The unrestricted token
    /// makes the 404 the route's absence rather than a scope's.
    /// </remarks>
    [Fact]
    public async Task NoTokenReadiesAPrinterDirectly()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.ForceState(DeviceState.Finished));

        using HttpClient client = await ScopedClientAsync(userId, CapabilitySet.Everything);

        using HttpResponseMessage response = await client.PutAsync($"/api/v1/printers/{uuid}/command/ready",
                                                                   null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no such route, rather than one that refuses");

        fake.Device.State.Should().Be(DeviceState.Finished, "a finished printer accepts SET_PRINTER_READY, so Ready would mean it was asked");
        fake.ReceivedCommands.Should().BeEmpty("nothing may reach the printer that could ready it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token scoped to <c>ControlPrinter</c> sets a CORE One's light, and the brightness reads back
    /// on the telemetry route straight away - not when the printer next sends full telemetry, which
    /// a change to the light does not trigger.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToControlPrinterSetsTheLight()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(printerType: CoreOne);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/printers/{uuid}/command/lighting",
                                                                          new { intensity = 33 },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.Device.LedIntensity.Should().Be(32, "a 204 means the printer stored it - and 33% reads back through its byte as 32%");

        int? lighting = await LightingOfAsync(client, uuid);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        // Polled, because the writer flushes in batches: recorded at once, but not stored at once.
        while (lighting != 32 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            lighting = await LightingOfAsync(client, uuid);
        }

        lighting.Should().Be(32, "the accepted brightness is recorded as the printer will report it");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A brightness past 100 is the caller's mistake, refused before the printer hears it - which
    /// would store 101 as a light all but off.
    /// </summary>
    [Fact]
    public async Task ABrightnessPastAHundredIsABadRequestAndReachesNoPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(printerType: CoreOne);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/printers/{uuid}/command/lighting",
                                                                          new { intensity = 101 },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame");
        fake.Device.SideLedBrightness.Should().Be(255);

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A printer with no lighting - an MK3.5 that has never reported a brightness - is a conflict, and
    /// is not sent what it would answer "Missing or broken parameters".
    /// </summary>
    [Fact]
    public async Task APrinterWithoutLightingIsAConflictAndReachesNoPrinter()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.SideLedsSupported = false);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/printers/{uuid}/command/lighting",
                                                                          new { intensity = 40 },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fake.ReceivedCommands.Should().BeEmpty("a printer without the strips is refused here, not by its firmware");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A printer that refuses the setting is a conflict carrying its own words, not a 204 - here a
    /// CORE One whose build answers as one without the strips does.
    /// </summary>
    [Fact]
    public async Task ALightThePrinterRefusesIsAConflictInItsOwnWords()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.SideLedsSupported = false, printerType: CoreOne);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ControlPrinter]);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/printers/{uuid}/command/lighting",
                                                                          new { intensity = 40 },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DetailOfAsync(response)).Should().Be("Missing or broken parameters", "the printer's own words");
        (await CommandOfAsync(response)).Should().Be("lighting");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>ControlPrinter</c> is refused the light with a 403 naming
    /// it, and the printer hears nothing.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutControlPrinterCannotSetTheLight()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(printerType: CoreOne);

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/printers/{uuid}/command/lighting",
                                                                          new { intensity = 40 },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("ControlPrinter",
                                                         "a scope refusal names the capability, so the fix is a new token");
        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the frame, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>The <c>lighting</c> the telemetry route reports for a printer.</summary>
    private static async Task<int?> LightingOfAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/printers/{uuid}/telemetry",
                                                                   TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("lighting") is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;
    }

    /// <summary>The <c>detail</c> of a ProblemDetails answer, where a refusal explains itself.</summary>
    private static async Task<string> DetailOfAsync(HttpResponseMessage response)
    {
        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("detail").GetString() ?? string.Empty;
    }

    /// <summary>The <c>command</c> a refusal names - the act, as the API names it.</summary>
    private static async Task<string> CommandOfAsync(HttpResponseMessage response)
    {
        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("command").GetString() ?? string.Empty;
    }

    private async Task<Guid> UuidOfAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Printers
                            .Where(printer => printer.Id == printerId)
                            .Select(printer => printer.Uuid)
                            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A client authenticating with a freshly minted token carrying exactly this scope.</summary>
    private async Task<HttpClient> ScopedClientAsync(long userId, IEnumerable<Capability> scope)
    {
        string plaintext = await MintTokenAsync(userId, scope);

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        return client;
    }

    /// <summary>A freshly minted token carrying exactly this scope, as its plaintext.</summary>
    private async Task<string> MintTokenAsync(long userId, IEnumerable<Capability> scope)
    {
        using IServiceScope serviceScope = _factory.Services.CreateScope();
        ApiTokenService tokens = serviceScope.ServiceProvider.GetRequiredService<ApiTokenService>();

        (_, string plaintext) = await tokens.CreateAsync(userId, "dispatch-e2e", scope, TestContext.Current.CancellationToken);

        return plaintext;
    }

    /// <summary>Uploads as the printer's owner over a cookie session, so the scoped token under test
    /// carries no file capability it does not need.</summary>
    private async Task UploadAsOwnerAsync(long userId, string name)
    {
        HSUser owner = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);

        using HttpClient client = await EnrolmentFlowHelper.SignInAsAsync(_factory, owner);

        await UploadAsync(client, name);
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StringContent body = new(FileContent);

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the upload is setup for this test, not what it verifies");
    }

    /// <summary>An open print on the printer, queued by <paramref name="queuedBy"/>, as the queue's loop writes one.</summary>
    private async Task OpenPrintAsync(Guid uuid, long queuedBy)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == uuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = Guid.NewGuid(),
            FileName = "theirs.gcode",
            QueuedByUserId = queuedBy,
            State = PrintState.Printing,
            StartedAt = DateTimeOffset.UtcNow,
            CommandedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    /// <summary>An enrolled, connected printer - every test here needs a live socket, either to
    /// receive a command or to prove nothing arrived.</summary>
    /// <param name="configure">Seeds the device before it connects.</param>
    /// <param name="policyFactory">
    /// How the fake answers commands, when the firmware-faithful default is not what the test is
    /// about. Takes the identity because a policy wrapping <see cref="FirmwareFaithfulPolicy"/>
    /// needs it, and it does not exist until the printer is enrolled.
    /// </param>
    /// <param name="printerType">
    /// The model the printer reports, as a <c>printer_type</c> triple; an MK3.5 when null. Returns
    /// only once the server has stored it, since a page or a service reading the model would
    /// otherwise race the printer's <c>INFO</c>.
    /// </param>
    private async Task<(Guid uuid, long userId, FakePrinterClient fake, Task run)> ConnectedPrinterAsync(
        Action<FakePrinterClient>? configure = null,
        Func<PrinterIdentity, CommandAnswerPolicy>? policyFactory = null,
        string? printerType = null)
    {
        PrinterIdentity random = PrinterIdentity.CreateRandom();
        PrinterIdentity? model = printerType is null ?
            null :
            new PrinterIdentity { Fingerprint = random.Fingerprint, SerialNumber = random.SerialNumber, PrinterType = printerType };

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory, model);

        FakePrinterOptions options = new() { Policy = policyFactory?.Invoke(identity) };

        FakePrinterClient fake = new(identity, TimeProvider.System, options) { Token = token };
        configure?.Invoke(fake);

        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        await FakePrinterConnections.WaitUntilConnectedAsync(_factory, printerId);

        if (printerType is not null)
        {
            (await FakePrinterConnections.WaitUntilAsync(() => ModelOf(printerId) == printerType, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("the printer's INFO, sent on connecting, names its model");
        }

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Guid uuid = (await context.Printers.SingleAsync(printer => printer.Id == printerId,
                                                        TestContext.Current.CancellationToken)).Uuid;

        return (uuid, userId, fake, run);
    }

    /// <summary>The model the server has stored for a printer.</summary>
    private string? ModelOf(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return context.Printers.AsNoTracking().Single(printer => printer.Id == printerId).Model;
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

    /// <summary>
    /// Firmware-faithful for everything but <c>SEND_FILE_INFO</c>, which it answers with whatever
    /// payload the test hands it - for the answers a real printer does not give and the endpoint
    /// still has to survive.
    /// </summary>
    private sealed class FileInfoAnsweredWith : CommandAnswerPolicy
    {
        private readonly CommandAnswerPolicy _inner;
        private readonly Func<ServerCommandFrame, FakeDevice, byte[]> _payload;

        public FileInfoAnsweredWith(CommandAnswerPolicy inner, Func<ServerCommandFrame, FakeDevice, byte[]> payload)
        {
            _inner = inner;
            _payload = payload;
        }

        public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
        {
            return frame.TryGetJsonCommandName() == "SEND_FILE_INFO" ?
                [new PlannedReply(_payload(frame, device))] :
                _inner.Answer(frame, device);
        }
    }
}
