using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Http;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A file sent straight to a printer - from the API or the Files page - goes under the same drive
/// name the queue would give it, so two members' files of one name never share a path; and a file
/// overwritten since it was sent replaces its older copy under that name.
/// </summary>
/// <remarks>
/// <b>The case this pins is the one a direct send used to leave open.</b> It put a file on the drive
/// under its own name and recorded nothing, so the next transfer of another member's file of that
/// name could not see it - and the queue, refused by the printer, adopted it when the sizes matched.
/// Both sends here go to a genuinely connected fake, and the assertion is the path the printer was
/// actually asked to store the file under.
/// </remarks>
public sealed class DirectSendDriveNameTests : IAsyncLifetime
{
    private const string FileContent = "G28 ; home\n";

    /// <summary>What the file is overwritten with - a different length, so the drive can tell them apart.</summary>
    private const string NewerContent = "G28 ; home\nG1 Z10 ; lift\n";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("direct-send-names");
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
    /// The owner sends <c>part.gcode</c> over the API; a second member's <c>part.gcode</c>, sent the
    /// same way, reaches the drive under the member's name.
    /// </summary>
    [Fact]
    public async Task ASecondMembersFileOfTheSameNameIsSentUnderTheirNameOverTheApi()
    {
        // Arrange
        (Guid uuid, long ownerId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser member, HttpClient memberCookies) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "direct-send-member@example.com");

        using (memberCookies)
        {
            await JoinAsync(member.Id, await TeamOfAsync(uuid), CapabilityPresets.Operator);
            await UploadAsync(memberCookies, "part.gcode");
        }

        await UploadAsOwnerAsync(ownerId, "part.gcode");
        await SendOverApiAsync(ownerId, uuid, "part.gcode");
        (await WaitForTransferToAsync(fake, "/usb/part.gcode")).Should().BeTrue("the first file of a name keeps it");

        // Act
        await SendOverApiAsync(member.Id, uuid, "part.gcode");

        // Assert
        (await WaitForTransferToAsync(fake, $"/usb/part ({member.UserName}).gcode"))
            .Should().BeTrue("the owner's file already holds the name on that drive");

        await EndRunAsync(fake, run);
    }

    /// <summary>The same through the Files page's Send, which shares the rule rather than a copy of it.</summary>
    [Fact]
    public async Task ASecondMembersFileOfTheSameNameIsSentUnderTheirNameFromTheFilesPage()
    {
        // Arrange
        (Guid uuid, long ownerId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        (HSUser member, HttpClient memberCookies) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "direct-send-page@example.com");

        using (memberCookies)
        {
            await JoinAsync(member.Id, await TeamOfAsync(uuid), CapabilityPresets.Operator);
            await UploadAsync(memberCookies, "part.gcode");

            await UploadAsOwnerAsync(ownerId, "part.gcode");
            await SendOverApiAsync(ownerId, uuid, "part.gcode");
            (await WaitForTransferToAsync(fake, "/usb/part.gcode")).Should().BeTrue();

            // Act
            string page = await (await memberCookies.GetAsync("/Files", TestContext.Current.CancellationToken))
                                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
            {
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
                new("printerUuid", uuid.ToString()),
            });

            using HttpResponseMessage response = await memberCookies.PostAsync(
                "/Files?handler=Send&name=part.gcode", form, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        // Assert
        (await WaitForTransferToAsync(fake, $"/usb/part ({member.UserName}).gcode"))
            .Should().BeTrue("the owner's file already holds the name on that drive");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A file overwritten since it was sent is sent again over the API: the printer's older copy is
    /// deleted first, and the newer bytes are what arrive.
    /// </summary>
    /// <remarks>
    /// <b>The delete is the assertion that matters here.</b> Firmware refuses a transfer onto a name
    /// it already holds, which used to leave no way to get the newer version there at all; this fake
    /// replaces the file instead, so only the delete it was sent shows the server doing its part.
    /// </remarks>
    [Fact]
    public async Task AnOverwrittenFileReplacesItsOlderCopyOnTheDrive()
    {
        // Arrange - sent and arrived, then overwritten
        (Guid uuid, long ownerId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        await UploadAsOwnerAsync(ownerId, "part.gcode");
        await SendOverApiAsync(ownerId, uuid, "part.gcode");
        (await WaitForTransferToAsync(fake, "/usb/part.gcode")).Should().BeTrue();
        FakeTransfer first = fake.Device.LastTransfer!;

        await UploadAsOwnerAsync(ownerId, "part.gcode", NewerContent, overwrite: true);

        // Act
        await SendOverApiAsync(ownerId, uuid, "part.gcode");

        // Assert
        (await FakePrinterConnections.WaitUntilAsync(
                () =>
                {
                    FakeTransfer? last = fake.Device.LastTransfer;

                    return last is not null && !ReferenceEquals(last, first) && last.Path == "/usb/part.gcode";
                },
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the newer version is sent under the same name");

        fake.Device.LastTransfer!.TotalSize.Should().Be(NewerContent.Length, "what arrived is the newer version");
        fake.ReceivedCommands.Where(frame => frame.TryGetJsonCommandName() == "DELETE_FILE")
            .Select(frame => PathArgument.TryParse(frame.Payload))
            .Should().Equal(["/usb/part.gcode"], "the older copy is deleted before the newer one is offered");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// An older copy the printer is printing is kept, and the send is refused with the printer's words
    /// rather than sent anywhere else.
    /// </summary>
    [Fact]
    public async Task AnOlderCopyThePrinterIsPrintingIsKeptAndTheSendRefused()
    {
        // Arrange - sent, arrived, now printing, and overwritten meanwhile
        (Guid uuid, long ownerId, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();
        await UploadAsOwnerAsync(ownerId, "part.gcode");
        await SendOverApiAsync(ownerId, uuid, "part.gcode");
        (await WaitForTransferToAsync(fake, "/usb/part.gcode")).Should().BeTrue();

        fake.Device.StartPrint(jobId: 5, path: "/usb/part.gcode");
        await UploadAsOwnerAsync(ownerId, "part.gcode", NewerContent, overwrite: true);

        // Act
        using HttpClient client = await ScopedClientAsync(ownerId, [Capability.Print]);
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name = "part.gcode" },
                                                                          TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("File is busy", "the printer's own reason is what tells somebody to wait");
        fake.Device.Storage.Find("/usb/part.gcode").Should().NotBeNull("the copy being printed is not touched");

        await EndRunAsync(fake, run);
    }

    private async Task SendOverApiAsync(long userId, Guid uuid, string name)
    {
        using HttpClient client = await ScopedClientAsync(userId, [Capability.Print]);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/files",
                                                                          new { name },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "204 means the printer accepted the transfer");
    }

    /// <summary>Waits until the printer has finished a transfer to <paramref name="path"/>.</summary>
    private static Task<bool> WaitForTransferToAsync(FakePrinterClient fake, string path)
    {
        return FakePrinterConnections.WaitUntilAsync(() => fake.Device.LastTransfer?.Path == path,
                                                     TimeSpan.FromSeconds(30));
    }

    private async Task<HttpClient> ScopedClientAsync(long userId, IEnumerable<Capability> scope)
    {
        string plaintext;

        using (IServiceScope serviceScope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = serviceScope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, plaintext) = await tokens.CreateAsync(userId, "direct-send-e2e", scope, TestContext.Current.CancellationToken);
        }

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthorizationSchemes.Bearer, plaintext);

        return client;
    }

    private async Task UploadAsOwnerAsync(long userId, string name, string content = FileContent, bool overwrite = false)
    {
        HSUser owner = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);

        using HttpClient client = await EnrolmentFlowHelper.SignInAsAsync(_factory, owner);

        await UploadAsync(client, name, content, overwrite);
    }

    private static async Task UploadAsync(HttpClient client, string name, string content = FileContent, bool overwrite = false)
    {
        using StringContent body = new(content);

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}{(overwrite ? "?overwrite=true" : string.Empty)}", body,
                                  TestContext.Current.CancellationToken);

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

    private async Task<(Guid uuid, long userId, FakePrinterClient fake, Task run)> ConnectedPrinterAsync()
    {
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterClient fake = new(identity, TimeProvider.System) { Token = token };

        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        await FakePrinterConnections.WaitUntilConnectedAsync(_factory, printerId);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Guid uuid = (await context.Printers.SingleAsync(printer => printer.Id == printerId,
                                                        TestContext.Current.CancellationToken)).Uuid;

        return (uuid, userId, fake, run);
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fake.ReplyFault.Should().BeNull("a faulted fake would invalidate what this test claims about the server");

        await fake.DisposeAsync();
    }
}
