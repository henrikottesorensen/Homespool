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
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A file sent straight to a printer - from the API or the Files page - goes under the same drive
/// name the queue would give it, so two members' files of one name never share a path.
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        return client;
    }

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
