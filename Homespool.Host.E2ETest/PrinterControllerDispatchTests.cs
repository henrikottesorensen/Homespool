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
/// <c>PrinterController</c>'s dispatch endpoints - send a file, print, browse storage - each once
/// with a token whose scope names the capability and once with one that does not, against a
/// genuinely connected printer.
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

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("controller-dispatch");
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
    /// A token scoped to <c>Print</c> starts a print of a file already on the printer, and the
    /// printer actually starts.
    /// </summary>
    [Fact]
    public async Task ATokenScopedToPrintStartsAPrint()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/model.gcode", 4242, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/print",
                                                                          new { path = "/usb/model.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "204 means the printer answered and did not refuse");

        fake.Device.State.Should().Be(DeviceState.Printing,
                                      "the command must have executed, not merely been accepted by the API");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>Print</c> starts nothing, and the 403 names it.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutPrintCannotStartAPrintAndTheRefusalNamesIt()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/model.gcode", 4242, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ViewPrinter]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/print",
                                                                          new { path = "/usb/model.gcode" },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailOfAsync(response)).Should().Contain("Print");

        fake.Device.State.Should().Be(DeviceState.Idle, "a refused print changes nothing at the machine");
        fake.ReceivedCommands.Should().BeEmpty();

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
            await client.GetAsync($"/api/v1/printers/{uuid}/storage/usb", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        payload.RootElement.GetProperty("entries").EnumerateArray()
               .Single().GetProperty("name").GetString().Should().Be("lampshade.gcode");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A token whose scope does not name <c>ControlPrinter</c> is refused the listing, and the
    /// printer is asked nothing - browsing reads, but by making the machine go and work.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not asserted: whether the refusal names the capability.</b> Every other scope
    /// refusal does, and today this one does not - the endpoint gates on the boolean ask rather
    /// than the throwing one, so its 403 says only that browsing is not allowed. Whether that
    /// sentence or the documented promise moves is an open decision, and pinning either wording
    /// here would take it by accident.
    /// </remarks>
    [Fact]
    public async Task ATokenWithoutControlPrinterCannotBrowseStorage()
    {
        (Guid uuid, long userId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync(
            configure: f => f.Device.Storage.AddFile("/usb/lampshade.gcode", 7647560, 1764804970));

        using HttpClient client = await ScopedClientAsync(userId, [Capability.ViewPrinter]);

        using HttpResponseMessage response =
            await client.GetAsync($"/api/v1/printers/{uuid}/storage/usb", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        fake.ReceivedCommands.Should().BeEmpty("the refusal must come before the command, not after it");

        await EndRunAsync(fake, run);
    }

    /// <summary>The <c>detail</c> of a ProblemDetails answer, where a refusal explains itself.</summary>
    private static async Task<string> DetailOfAsync(HttpResponseMessage response)
    {
        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("detail").GetString() ?? string.Empty;
    }

    /// <summary>A client authenticating with a freshly minted token carrying exactly this scope.</summary>
    private async Task<HttpClient> ScopedClientAsync(long userId, IEnumerable<Capability> scope)
    {
        string plaintext;

        using (IServiceScope serviceScope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = serviceScope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, plaintext) = await tokens.CreateAsync(userId, "dispatch-e2e", scope, TestContext.Current.CancellationToken);
        }

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        return client;
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
    private async Task<(Guid uuid, long userId, FakePrinterClient fake, Task run)> ConnectedPrinterAsync(
        Action<FakePrinterClient>? configure = null)
    {
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterClient fake = new(identity, TimeProvider.System) { Token = token };
        configure?.Invoke(fake);

        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        await FakePrinterConnections.WaitUntilConnectedAsync(_factory, printerId);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Guid uuid = (await context.Printers.SingleAsync(printer => printer.Id == printerId,
                                                        TestContext.Current.CancellationToken)).Uuid;

        return (uuid, userId, fake, run);
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
