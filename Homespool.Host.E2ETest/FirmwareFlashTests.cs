using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Controllers;
using Homespool.Host.Firmware;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Queue;
using Homespool.Host.Test;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Installing firmware on a connected printer, through the page and the real transfer and command
/// paths: the image goes over, the one flash line follows, and the printer coming back on the image's
/// version is what finishes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The host verifies with the run's own key</b>, swapped in for Prusa's, because an image the test
/// can build must be one the host accepts; that Prusa's key checks Prusa's images is the verifier's
/// own tests' business.
/// </para>
/// <para>
/// <b>The fake printer does not reboot.</b> When the flash line arrives the test does what the printer
/// would: drops the connection and comes back as a new connection reporting the image's version.
/// </para>
/// </remarks>
public sealed class FirmwareFlashTests : IAsyncLifetime
{
    private const string Before = "6.8.1+12345";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("firmware-flash");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestFirmwareImages.Verifier);

            // Seconds where a printer needs minutes, so a printer that never comes back on the image's
            // version fails within the test.
            services.AddSingleton(new FirmwareFlashTimings(PollInterval: TimeSpan.FromMilliseconds(200),
                                                           ArrivalTimeout: TimeSpan.FromSeconds(30),
                                                           RestartTimeout: TimeSpan.FromSeconds(15),
                                                           PathGrace: TimeSpan.FromSeconds(2)));
        }));

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task AnImageIsSentFlashedAndCleanedUpOnceThePrinterIsBackOnItsVersion()
    {
        // Arrange - a Core One on 6.8.1, flashed with a stored 7.0.0 image, restarting as the printer would
        (int printerId, _, _, FirmwareImage image, PrinterIdentity identity, string token) = await FlashedCoreOneAsync();

        // Act - it comes back on the image's version
        await using FakePrinterClient back = await ConnectAsync(Restarted(identity, image.Header.Version), token);
        Task backRun = back.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        FirmwareFlashStatus finished = await FinishedAsync(printerId);

        finished.Stage.Should().Be(FirmwareFlashStage.Done, finished.Problem?.Key);
        back.ReceivedCommands.Where(frame => frame.TryGetJsonCommandName() == "DELETE_FILE")
            .Select(frame => PathArgument.TryParse(frame.Payload))
            .Should().Equal([FlashFirmware.DrivePath], "the image is removed from the drive once it is installed");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>().FilesOnPrinters
                        .AnyAsync(row => row.PrinterId == printerId, TestContext.Current.CancellationToken))
                .Should().BeFalse("nothing of the image is left recorded on that drive");
        }

        await back.CloseAsync(TestContext.Current.CancellationToken);
        await backRun.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        back.ReplyFault.Should().BeNull();
    }

    /// <summary>
    /// A printer that comes back still on its old version - the bootloader refused the image, or never
    /// found it - is a failed flash, said as one, and the image is left on the drive for another try.
    /// </summary>
    [Fact]
    public async Task APrinterThatComesBackOnItsOldVersionIsAFailedFlash()
    {
        // Arrange
        (int printerId, _, _, _, PrinterIdentity identity, string token) = await FlashedCoreOneAsync();

        // Act - it comes back on the version it had
        await using FakePrinterClient back = await ConnectAsync(Restarted(identity, Before), token);
        Task backRun = back.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        FirmwareFlashStatus finished = await FinishedAsync(printerId);

        finished.Stage.Should().Be(FirmwareFlashStage.Failed);
        finished.Problem!.Key.Should().Be("Firmware_FailedNotBack");
        back.ReceivedCommands.Should().NotContain(frame => frame.TryGetJsonCommandName() == "DELETE_FILE",
                                                  "an image that was not installed stays for the next attempt");

        await back.CloseAsync(TestContext.Current.CancellationToken);
        await backRun.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The case a fixed name on the drive invites: 7.0.0 is installed but its delete never reaches the
    /// printer, so <c>FIRMWARE.BBF</c> is still there when a later build comes along. The record of it
    /// is kept, and the next flash deletes it before sending - rather than meeting "File already
    /// exists".
    /// </summary>
    [Fact]
    public async Task AnImageWhoseDeleteDidNotGoThroughIsClearedBeforeTheNextFlash()
    {
        // Arrange - installed, but the printer drops the connection when told to delete the image
        Flashed first = await FlashedCoreOneAsync();

        await using (FakePrinterClient dropping = await ConnectAsync(Restarted(first.Identity, first.Image.Header.Version),
                                                                     first.Token, new DisconnectOnCommandPolicy()))
        {
            Task droppingRun = dropping.RunAsync(TestContext.Current.CancellationToken);

            (await FinishedAsync(first.PrinterId)).Stage.Should().Be(FirmwareFlashStage.Done, "the printer runs the image whatever the delete did");
            (await RecordedImagesAsync(first.PrinterId)).Should().Be(1, "a delete that did not go through keeps the record");

            await droppingRun.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        // The drive still holds the image when the printer next connects.
        await using FakePrinterClient fake = await ConnectAsync(Restarted(first.Identity, first.Image.Header.Version), first.Token);
        fake.Device.Storage.AddFile(FlashFirmware.DrivePath, first.Image.Size, modified: 0);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);
        await WaitForModelAsync(first.PrinterId);

        FirmwareImage later = await StoreAsync(first.OwnerId, first.PrinterId, build: 17000, name: "COREONE_firmware_7.0.1.bbf");

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, first.OwnerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        // Act
        await PostFlashAsync(owner, first.Uuid, later.Digest);

        // Assert - the old image is deleted, then the new one sent, then flashed
        (await FakePrinterConnections.WaitUntilAsync(() => fake.ReceivedCommands.Any(IsFlashLine), TimeSpan.FromSeconds(60)))
            .Should().BeTrue("the later image arrives and is flashed; the flash reads {0} {1}",
                             _factory.Services.GetRequiredService<FirmwareFlashes>().For(first.PrinterId)?.Stage,
                             _factory.Services.GetRequiredService<FirmwareFlashes>().For(first.PrinterId)?.Problem?.Key);

        List<string?> commands = [.. fake.ReceivedCommands.Select(frame => frame.TryGetJsonCommandName() ?? frame.PayloadText())];
        commands.IndexOf("DELETE_FILE").Should().BeGreaterThanOrEqualTo(0);
        commands.IndexOf("DELETE_FILE").Should().BeLessThan(commands.FindIndex(command => command is "START_CONNECT_DOWNLOAD" or "START_ENCRYPTED_DOWNLOAD"),
                                                            "the earlier image is deleted before the later one is offered");
        fake.Device.LastTransfer!.Path.Should().Be(FlashFirmware.DrivePath, "the later image arrived under the one name");

        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An earlier image the printer will not delete stops the flash with that said, and stays recorded
    /// for the next try - sending anyway would only meet "File already exists".
    /// </summary>
    [Fact]
    public async Task AnEarlierImageThePrinterWillNotDeleteStopsTheFlash()
    {
        // Arrange - 7.0.0 installed and still recorded on the drive, as above
        Flashed first = await FlashedCoreOneAsync();

        await using (FakePrinterClient dropping = await ConnectAsync(Restarted(first.Identity, first.Image.Header.Version),
                                                                     first.Token, new DisconnectOnCommandPolicy()))
        {
            Task droppingRun = dropping.RunAsync(TestContext.Current.CancellationToken);
            await FinishedAsync(first.PrinterId);
            await droppingRun.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        // A printer that drops the connection on any command cannot delete anything.
        await using FakePrinterClient fake = await ConnectAsync(Restarted(first.Identity, first.Image.Header.Version),
                                                                first.Token, new DisconnectOnCommandPolicy());
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);
        await WaitForModelAsync(first.PrinterId);

        FirmwareImage later = await StoreAsync(first.OwnerId, first.PrinterId, build: 17000, name: "COREONE_firmware_7.0.1.bbf");

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, first.OwnerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        // Act
        await PostFlashAsync(owner, first.Uuid, later.Digest);

        // Assert
        FirmwareFlashStatus finished = await FinishedAsync(first.PrinterId);

        finished.Stage.Should().Be(FirmwareFlashStage.Failed);
        finished.Problem!.Key.Should().Be("Firmware_FailedEarlierImageKept");
        (await RecordedImagesAsync(first.PrinterId)).Should().Be(1, "the image it could not delete is still recorded");

        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A <c>FIRMWARE.BBF</c> Homespool has no record of is not overwritten and not deleted: the flash
    /// stops and says somebody at the printer must remove it.
    /// </summary>
    [Fact]
    public async Task AFirmwareFileHomespoolDidNotPutThereIsLeftAlone()
    {
        // Arrange
        (PrinterIdentity identity, string token, int printerId, long ownerId) = await EnrolCoreOneAsync();
        await using FakePrinterClient fake = await ConnectAsync(identity, token);
        fake.Device.Storage.AddFile(FlashFirmware.DrivePath, 1234, modified: 0);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);
        await WaitForModelAsync(printerId);

        FirmwareImage image = await StoreAsync(ownerId, printerId);
        Guid uuid = await UuidOfAsync(printerId);

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, ownerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        // Act
        await PostFlashAsync(owner, uuid, image.Digest);

        // Assert
        FirmwareFlashStatus finished = await FinishedAsync(printerId);

        finished.Stage.Should().Be(FirmwareFlashStage.Failed);
        finished.Problem!.Key.Should().Be("Firmware_FailedUnknownImage");
        fake.ReceivedCommands.Select(frame => frame.TryGetJsonCommandName())
            .Should().NotContain(["DELETE_FILE", "START_CONNECT_DOWNLOAD", "START_ENCRYPTED_DOWNLOAD"]);
        fake.Device.Storage.Find(FlashFirmware.DrivePath)!.Size.Should().Be(1234, "a file Homespool cannot vouch for is left as it is");

        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private async Task<int> RecordedImagesAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .FilesOnPrinters
                          .CountAsync(row => row.PrinterId == printerId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A connected, idle Core One on <see cref="Before"/>, sent a stored image through the page's
    /// Install - returned once the flash line has reached it, and with that connection gone, as a
    /// printer's goes when it restarts.
    /// </summary>
    private async Task<Flashed> FlashedCoreOneAsync()
    {
        (PrinterIdentity identity, string token, int printerId, long ownerId) = await EnrolCoreOneAsync();
        await using FakePrinterClient fake = await ConnectAsync(identity, token);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);
        await WaitForModelAsync(printerId);

        FirmwareImage image = await StoreAsync(ownerId, printerId);
        Guid uuid = await UuidOfAsync(printerId);

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, ownerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        string started = await PostFlashAsync(owner, uuid, image.Digest);
        started.Should().Contain("Installing firmware 7.0.0+16903", "the printer is connected, idle and has nothing queued");

        bool flashed = await FakePrinterConnections.WaitUntilAsync(
            () => fake.ReceivedCommands.Any(IsFlashLine), TimeSpan.FromSeconds(60));

        // Where the flash stopped, if it did, is the first thing a failure here needs to say.
        FirmwareFlashStatus? during = _factory.Services.GetRequiredService<FirmwareFlashes>().For(printerId);
        flashed.Should().BeTrue("the flash follows the image's arrival; the flash reads {0} {1}", during?.Stage, during?.Problem?.Key);
        fake.Device.Storage.Find(FlashFirmware.DrivePath).Should().NotBeNull("the image is on the drive under the one name");

        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        return new Flashed(printerId, ownerId, uuid, image, identity, token);
    }

    private static PrinterIdentity Restarted(PrinterIdentity identity, string firmware)
    {
        return new PrinterIdentity
        {
            Fingerprint = identity.Fingerprint,
            SerialNumber = identity.SerialNumber,
            PrinterType = identity.PrinterType,
            Firmware = firmware,
        };
    }

    private async Task<FirmwareFlashStatus> FinishedAsync(int printerId)
    {
        FirmwareFlashes flashes = _factory.Services.GetRequiredService<FirmwareFlashes>();

        (await FakePrinterConnections.WaitUntilAsync(() => flashes.For(printerId)?.IsRunning == false, TimeSpan.FromSeconds(60)))
            .Should().BeTrue("the flash ends within its restart wait");

        return flashes.For(printerId)!;
    }

    /// <summary>A printer with nothing connected is refused at once, with the reason, and nothing starts.</summary>
    [Fact]
    public async Task APrinterThatIsNotConnectedIsRefused()
    {
        // Arrange
        (_, _, int printerId, long ownerId) = await EnrolCoreOneAsync();
        await SetModelAsync(printerId);
        FirmwareImage image = await StoreAsync(ownerId, printerId);
        Guid uuid = await UuidOfAsync(printerId);

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, ownerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        // Act
        string page = await PostFlashAsync(owner, uuid, image.Digest);

        // Assert
        page.Should().Contain("is not connected, so no firmware can be sent to it.");
        _factory.Services.GetRequiredService<FirmwareFlashes>().For(printerId).Should().BeNull();
    }

    /// <summary>
    /// A connected, idle printer with a print queued is refused: the queue could start that print
    /// between the image arriving and the flash.
    /// </summary>
    [Fact]
    public async Task APrinterWithAPrintQueuedIsRefused()
    {
        // Arrange
        (PrinterIdentity identity, string token, int printerId, long ownerId) = await EnrolCoreOneAsync();
        await using FakePrinterClient fake = await ConnectAsync(identity, token);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);
        await WaitForModelAsync(printerId);

        FirmwareImage image = await StoreAsync(ownerId, printerId);
        await QueueAPrintAsync(ownerId, printerId);
        Guid uuid = await UuidOfAsync(printerId);

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, ownerId));
        await EnrolmentFlowHelper.ReauthenticateAsync(owner);

        // Act
        string page = await PostFlashAsync(owner, uuid, image.Digest);

        // Assert
        page.Should().Contain("has prints queued. Clear its queue first");
        _factory.Services.GetRequiredService<FirmwareFlashes>().For(printerId).Should().BeNull();
        fake.ReceivedCommands.Should().NotContain(frame => frame.TryGetJsonCommandName() == "START_ENCRYPTED_DOWNLOAD" ||
                                                           frame.TryGetJsonCommandName() == "START_CONNECT_DOWNLOAD");

        await fake.CloseAsync(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Without a recent proof the post goes to prove first, and nothing starts.</summary>
    [Fact]
    public async Task InstallingWantsARecentProof()
    {
        // Arrange
        (_, _, int printerId, long ownerId) = await EnrolCoreOneAsync();
        await SetModelAsync(printerId);
        FirmwareImage image = await StoreAsync(ownerId, printerId);
        Guid uuid = await UuidOfAsync(printerId);

        using HttpClient owner = await EnrolmentFlowHelper.SignInAsAsync(_factory, await EnrolmentFlowHelper.FindUserAsync(_factory, ownerId));
        string form = await GetAsync(owner, $"/Printers/Firmware/{uuid}");

        // Act
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(form),
            ["digest"] = image.Digest,
        });

        using HttpResponseMessage response = await owner.PostAsync($"/Printers/Firmware/{uuid}?handler=Flash", content,
                                                                   TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Account/Reauthenticate");
        form.Should().Contain("Confirm it's you to install", "the page offers the way to prove rather than a button that would be refused");
        _factory.Services.GetRequiredService<FirmwareFlashes>().For(printerId).Should().BeNull();
    }

    private static bool IsFlashLine(ServerCommandFrame frame)
    {
        return frame.Kind is ServerCommandKind.Gcode or ServerCommandKind.ForcedGcode &&
               frame.PayloadText() == FlashFirmware.FlashLine;
    }

    private async Task<(PrinterIdentity identity, string token, int printerId, long userId)> EnrolCoreOneAsync()
    {
        PrinterIdentity random = PrinterIdentity.CreateRandom();

        return await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory, new PrinterIdentity
        {
            Fingerprint = random.Fingerprint,
            SerialNumber = random.SerialNumber,
            PrinterType = "7.1.0",
            Firmware = Before,
        });
    }

    private async Task<FakePrinterClient> ConnectAsync(PrinterIdentity identity, string token, CommandAnswerPolicy? policy = null)
    {
        // Telemetry, so the printer reports a status: without one it reads as unknown, which is not idle.
        FakePrinterClient fake = new(identity, TimeProvider.System, new FakePrinterOptions { TelemetrySource = new SyntheticTelemetrySource(), Policy = policy })
        {
            Token = token,
        };

        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);

        return fake;
    }

    /// <summary>
    /// Waits for the printer's first <c>INFO</c> to have named its model, which an image is matched to,
    /// and for a status reported since it connected - until then it reads as unknown, which is not idle.
    /// </summary>
    private async Task WaitForModelAsync(int printerId)
    {
        bool idle = await FakePrinterConnections.WaitUntilAsync(
            () =>
            {
                using IServiceScope scope = _factory.Services.CreateScope();

                return PhysicalChangeRules.IsAllowed(scope.ServiceProvider.GetRequiredService<QueueSnapshotReader>()
                                                          .ReadAsync(printerId, TestContext.Current.CancellationToken)
                                                          .GetAwaiter().GetResult().Status);
            },
            TimeSpan.FromSeconds(30));

        idle.Should().BeTrue("the fake printer reports an idle status");

        bool named = await FakePrinterConnections.WaitUntilAsync(
            () =>
            {
                using IServiceScope scope = _factory.Services.CreateScope();

                return scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                            .Printers.Any(printer => printer.Id == printerId && printer.Model == "7.1.0");
            },
            TimeSpan.FromSeconds(30));

        named.Should().BeTrue("the fake printer's INFO names its model");
    }

    /// <summary>For a printer that never connects: the model its INFO would have named.</summary>
    private async Task SetModelAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                   .Printers
                   .Where(printer => printer.Id == printerId)
                   .ExecuteUpdateAsync(set => set.SetProperty(printer => printer.Model, "7.1.0")
                                                 .SetProperty(printer => printer.Firmware, Before),
                                       TestContext.Current.CancellationToken);
    }

    /// <summary>Stores a Core One image - 7.0.0+16903 unless a later build is asked for.</summary>
    private async Task<FirmwareImage> StoreAsync(long userId,
                                                 int printerId,
                                                 ushort build = 16903,
                                                 string name = "COREONE_firmware_7.0.0.bbf")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        await using MemoryStream content = new(TestFirmwareImages.Build(build: build, seed: (byte)build), writable: false);

        return await scope.ServiceProvider.GetRequiredService<FirmwareImages>()
                          .StoreAsync(Caller.Unscoped(userId), printerId, name, content, TestContext.Current.CancellationToken);
    }

    /// <summary>One of the owner's files queued on the printer - indexed directly, as the queue would hold it.</summary>
    private async Task QueueAPrintAsync(long userId, int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HSFile file = new()
        {
            Type = FileType.GCode,
            UserId = userId,
            Name = "waiting-cube.gcode",
            Size = 1024,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        context.Files.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = printerId,
            FileId = file.Id,
            PrintUuid = Guid.NewGuid(),
            QueuedByUserId = userId,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> UuidOfAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .Printers
                          .Where(printer => printer.Id == printerId)
                          .Select(printer => printer.Uuid)
                          .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Posts the page's Install for <paramref name="digest"/> and returns the page the redirect lands on.</summary>
    private static async Task<string> PostFlashAsync(HttpClient client, Guid uuid, string digest)
    {
        string form = await GetAsync(client, $"/Printers/Firmware/{uuid}");

        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(form),
            ["digest"] = digest,
        });

        using HttpResponseMessage response = await client.PostAsync($"/Printers/Firmware/{uuid}?handler=Flash", content,
                                                                    TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().NotContain("Reauthenticate", "the proof was earned first");

        return await GetAsync(client, response.Headers.Location!.OriginalString);
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>What <see cref="FlashedCoreOneAsync"/> leaves: the printer, its owner, and the image it was flashed with.</summary>
    private sealed record Flashed(int PrinterId, long OwnerId, Guid Uuid, FirmwareImage Image, PrinterIdentity Identity, string Token);
}
