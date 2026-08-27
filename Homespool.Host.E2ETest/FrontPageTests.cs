using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Pages;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The front page's printer tiles, rendered the way a browser gets them.
/// </summary>
/// <remarks>
/// <para>
/// Razor compiles at request time, so a green build says nothing about whether this page renders.
/// These are what notice a broken partial, a handler no poll reaches, or a tile leaking a printer
/// the caller may not see.
/// </para>
/// <para>
/// <b>The polled handler is exercised as well as the page</b>, because the two render the same
/// partial by different routes and only one of them is covered by loading the page - which is
/// exactly the asymmetry that let a control strip vanish on refresh once.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FrontPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-frontpage-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A visitor who is not signed in gets the holding page. There is nobody to have printers, so
    /// there are no tiles - and the page must still say something rather than render an empty grid.
    /// </summary>
    [Fact]
    public async Task ShowsTheHoldingPageToAVisitorWhoIsNotSignedIn()
    {
        // Act
        using HttpClient anonymous = _factory.CreateClient();
        string page = await (await anonymous.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Homespool");
        page.Should().NotContain("printer-shortcuts", "there is nobody whose printers these would be");
    }

    /// <summary>
    /// The tiles carry the printer's name and its status, and the most-used printer comes first.
    /// </summary>
    [Fact]
    public async Task OrdersTilesByHowMuchTheCallerHasUsedEachPrinter()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-order@example.com");

        (int busy, int quiet) = SeedPrinters(user.Id, "Busy One", "Quiet One");
        SeedJobs(busy, user.Id, count: 3);
        SeedJobs(quiet, user.Id, count: 1);

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Busy One");
        page.Should().Contain("Quiet One");
        page.IndexOf("Busy One", StringComparison.Ordinal)
            .Should().BeLessThan(page.IndexOf("Quiet One", StringComparison.Ordinal),
                                 "the printer you use most comes first");

        client.Dispose();
    }

    /// <summary>
    /// A printer that has reported a chamber gets the closed-box drawing; one that has not gets the
    /// open frame. This is the evidence-not-lookup rule reaching the page.
    /// </summary>
    [Fact]
    public async Task DrawsAnEnclosedPrinterAsABoxAndAnOpenOneAsAFrame()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-shape@example.com");

        (int enclosed, int open) = SeedPrinters(user.Id, "Boxed", "Framed");
        SeedLiveState(enclosed, chamber: 31.5f);
        SeedLiveState(open, chamber: null);

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("printer-icon-enclosed");
        page.Should().Contain("printer-icon-open");

        client.Dispose();
    }

    /// <summary>
    /// <b>The poll renders the same tiles.</b> A handler that returned nothing, or that rendered a
    /// partial missing half its state, would leave a page that looks right until it refreshes itself.
    /// </summary>
    [Fact]
    public async Task ThePolledHandlerRendersTheSameTiles()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-poll@example.com");

        SeedPrinters(user.Id, "Polled One", "Polled Two");

        // Act
        string fragment = await (await client.GetAsync("/?handler=Tiles", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        fragment.Should().Contain("printer-shortcuts");
        fragment.Should().Contain("Polled One");
        fragment.Should().NotContain("<html", "the poll answers a fragment, not a whole page");

        client.Dispose();
    }

    /// <summary>
    /// <b>A disconnected printer shows its filament but not its progress.</b> The live state persists,
    /// so every reading on it outlives the connection; progress and the time left describe a print
    /// nobody can watch and are dropped, while what filament is loaded stays true with the power off.
    /// </summary>
    /// <remarks>
    /// This is the one rule on the tile that a reader would most reasonably assume the other way, and
    /// the failure it prevents is a page contradicting itself - "42%" above an <i>Offline</i> badge.
    /// </remarks>
    [Fact]
    public async Task DropsProgressForADisconnectedPrinterButKeepsItsFilament()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-stale@example.com");

        (int printer, _) = SeedPrinters(user.Id, "Switched Off", "Spare");
        SeedLiveState(printer, chamber: null, progress: 42, timeRemaining: 4320, material: "PETG");

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("PETG", "what is loaded stays true while the power is off");
        page.Should().NotContain("42%", "progress on a printer nobody can reach is a frozen reading");
        page.Should().NotContain("printer-plaque-progress", "and the bar goes with it");

        client.Dispose();
    }

    /// <summary>
    /// <b>The page actually wires the drop up.</b> Every other test here posts to the handlers
    /// directly, which passes perfectly well while the markup that would reach them is missing - and
    /// that is exactly what happened: a drop did nothing at all and nothing was red.
    /// </summary>
    [Fact]
    public async Task ThePageCarriesEverythingADropNeeds()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-wiring@example.com");

        SeedPrinters(user.Id, "Wired", "Spare");

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        // "tile-drop" without the extension: asp-append-version fingerprints the FILE NAME in
        // .NET 10, so this renders as tile-drop.<hash>.js.
        page.Should().Contain("tile-drop", "without the script a tile is only a link");
        page.Should().Contain("data-drop-target", "the tiles have to be targets");
        page.Should().Contain("data-drop-form", "the upload needs its form");
        page.Should().Contain("data-drop-dialog", "and the dialog needs somewhere to render");

        client.Dispose();
    }

    /// <summary>
    /// The clash question is answered from the reader's own tree, before anything is uploaded.
    /// </summary>
    [Fact]
    public async Task TheDropDialogNamesTheClashesAndThePrinter()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-clash@example.com");

        SeedPrinters(user.Id, "Drop Target", "Spare");
        Guid uuid = FirstUuid(user.Id, "Drop Target");

        await UploadAsync(client, "already-here.gcode");

        // Act
        string dialog = await PostFormAsync(client, "/?handler=Conflicts", new()
        {
            ["uuid"] = [uuid.ToString()],
            ["names"] = ["already-here.gcode", "brand-new.gcode"],
        });

        // Assert
        dialog.Should().Contain("Drop Target", "a drop onto the wrong tile has to be obvious");
        dialog.Should().Contain("already-here.gcode");
        dialog.Should().Contain("clash:already-here.gcode", "the clash is asked about");
        dialog.Should().NotContain("clash:brand-new.gcode", "a name nobody has does not need a question");

        client.Dispose();
    }

    /// <summary>
    /// <b>A file no printer would take is refused in words.</b> Dropping an STL used to do nothing
    /// whatsoever - the script kept its own list of extensions and discarded the rest without a
    /// sound, which reads as a broken page rather than a refused file.
    /// </summary>
    /// <remarks>
    /// The list lives on the server now, so this also pins that there is one list rather than two
    /// that can drift - the browser copy had gained a <c>.g</c> the store never accepted and lost the
    /// <c>.bgc</c> it did.
    /// </remarks>
    [Fact]
    public async Task SaysSoWhenTheFileIsNotOneAPrinterWouldTake()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-stl@example.com");

        SeedPrinters(user.Id, "Picky", "Spare");
        Guid uuid = FirstUuid(user.Id, "Picky");

        // Act
        string dialog = await PostFormAsync(client, "/?handler=Conflicts", new()
        {
            ["uuid"] = [uuid.ToString()],
            ["names"] = ["bracket.stl"],
        });

        // Assert
        dialog.Should().Contain("bracket.stl", "the refusal names the file");
        dialog.Should().NotContain("data-drop-action", "there is nothing to do with it, so nothing is offered");
    }

    /// <summary>
    /// <b>Readying is not offered for a printer nothing can talk to.</b> A printer can be enrolled,
    /// permit remote readying, and still be switched off at the wall - and the command needs a live
    /// link. Offering it anyway produced an unhandled exception and a blank page, after the file had
    /// already uploaded and queued.
    /// </summary>
    [Fact]
    public async Task DoesNotOfferReadyingWhenThePrinterIsNotConnected()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-offline@example.com");

        SeedPrinters(user.Id, "Switched Off", "Spare");
        AllowRemoteReady(user.Id, "Switched Off");
        Guid uuid = FirstUuid(user.Id, "Switched Off");

        // Act - nothing has ever connected in this fixture, so the printer is offline.
        string dialog = await PostFormAsync(client, "/?handler=Conflicts", new()
        {
            ["uuid"] = [uuid.ToString()],
            ["names"] = ["part.gcode"],
        });

        // Assert
        dialog.Should().Contain("data-drop-action", "uploading and queueing are still on offer");

        // The attribute, not the bare word: DropReadyAndPrint is "ready", which also names the step
        // that would carry it - so a substring match passes while proving nothing.
        dialog.Should().NotContain($"data-drop-action=\"{IndexModel.DropReadyAndPrint}\"",
                                   "a printer nobody can reach cannot be made ready");
        dialog.Should().NotContain("data-drop-goto=\"ready\"",
                                   "and the button that leads to it is not there either");

        client.Dispose();
    }

    /// <summary>
    /// <b>A drop that cannot finish still answers.</b> Even with the offer withheld above, a printer
    /// can go away between the dialog opening and the button being pressed - so the command is
    /// guarded and its refusal is reported, rather than escaping as a 500 and a blank page.
    /// </summary>
    [Fact]
    public async Task ReportsARefusedReadyRatherThanFailingTheRequest()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-refused@example.com");

        SeedPrinters(user.Id, "Goes Away", "Spare");
        AllowRemoteReady(user.Id, "Goes Away");
        Guid uuid = FirstUuid(user.Id, "Goes Away");

        // Act - posting the action the dialog would not have shown, which is what a stale page does.
        HttpResponseMessage response = await PostDropAsync(client, uuid, IndexModel.DropReadyAndPrint, "part.gcode");

        // Assert
        ((int)response.StatusCode).Should().BeLessThan(500, "a printer that will not take the command is not a server fault");

        client.Dispose();
    }

    /// <summary>A .bgc file is accepted, because the store accepts it - the browser used not to.</summary>
    [Fact]
    public async Task AcceptsEveryExtensionTheStoreAccepts()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-bgc@example.com");

        SeedPrinters(user.Id, "Takes Bgc", "Spare");
        Guid uuid = FirstUuid(user.Id, "Takes Bgc");

        // Act
        string dialog = await PostFormAsync(client, "/?handler=Conflicts", new()
        {
            ["uuid"] = [uuid.ToString()],
            ["names"] = ["part.bgc"],
        });

        // Assert
        dialog.Should().Contain("data-drop-action", "a file the store takes gets the questions");
    }

    /// <summary>
    /// <b>Ready-and-print is refused for a printer that does not permit remote readying</b>, whatever
    /// the browser sent. The dialog hides the button; this is the half that matters, because a button
    /// nobody rendered is still a request somebody can make.
    /// </summary>
    [Fact]
    public async Task RefusesReadyAndPrintWhenThePrinterDoesNotAllowRemoteReadying()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-ready@example.com");

        SeedPrinters(user.Id, "No Remote Ready", "Spare");
        Guid uuid = FirstUuid(user.Id, "No Remote Ready");

        // Act
        await PostDropAsync(client, uuid, "ready", "part.gcode");

        // Assert - behaviour rather than a status code, because Forbid() under cookie auth is a
        // redirect to the deny page and the number tells you less than the queue does.
        QueuedCount(uuid).Should().Be(0, "a refused drop must not have queued anything either");

        client.Dispose();
    }

    /// <summary>
    /// Tiles are scoped to the caller. Somebody else's printer is not yours to see, and a front page
    /// is a careless place to leak the shape of a rack.
    /// </summary>
    [Fact]
    public async Task DoesNotShowAPrinterTheCallerCannotSee()
    {
        // Arrange
        (HSUser alice, HttpClient aliceClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-alice@example.com");
        (HSUser bob, HttpClient bobClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-bob@example.com");

        SeedPrinters(alice.Id, "Alice Only", "Alice Spare");
        SeedPrinters(bob.Id, "Bob Only", "Bob Spare");

        // Act
        string page = await (await aliceClient.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Alice Only");
        page.Should().NotContain("Bob Only", "the front page is scoped to the signed-in user");

        aliceClient.Dispose();
        bobClient.Dispose();
    }

    /// <summary>Lets a seeded printer be readied from the app, which is off by default.</summary>
    private void AllowRemoteReady(long userId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Printer printer = context.Printers.First(candidate => candidate.Name == name);
        printer.RemoteReadyAllowed = true;
        context.SaveChanges();
    }

    /// <summary>The uuid of a seeded printer, by the name it was seeded with.</summary>
    private Guid FirstUuid(long userId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return context.Printers.First(printer => printer.Name == name).Uuid;
    }

    /// <summary>How many files are queued on a printer, read straight from the table.</summary>
    private int QueuedCount(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = context.Printers.First(printer => printer.Uuid == uuid).Id;

        return context.QueuedPrints.Count(job => job.PrinterId == printerId);
    }

    /// <summary>Puts a file in the caller's own tree, through the page a person would use.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task UploadAsync(HttpClient client, string name)
    {
        string page = await (await client.GetAsync("/Files", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Disposing the MultipartFormDataContent disposes the parts it owns, so neither the token nor
        // the bytes are disposed separately - the same note FilesPageTests carries.
        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new ByteArrayContent([1, 2, 3]), "file", name);

        // A successful upload redirects, and this client does not follow redirects - so "not an
        // error" is the check, not "2xx".
        HttpResponseMessage response =
            await client.PostAsync("/Files?handler=Upload", form, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should()
            .BeLessThan(400, "the upload is setup for this test, not what it verifies");
    }

    /// <summary>Posts a form to a handler, carrying the antiforgery token the front page rendered.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<string> PostFormAsync(HttpClient client,
                                                    string url,
                                                    Dictionary<string, string[]> fields)
    {
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");

        foreach ((string key, string[] values) in fields)
        {
            foreach (string value in values)
            {
                form.Add(new StringContent(value), key);
            }
        }

        HttpResponseMessage response = await client.PostAsync(url, form, TestContext.Current.CancellationToken);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Drives the drop handler the way the script does: files, a printer and an action.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostDropAsync(HttpClient client,
                                                                 Guid uuid,
                                                                 string action,
                                                                 string fileName)
    {
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StringContent(uuid.ToString()), "uuid");
        form.Add(new StringContent(action), "action");
        form.Add(new ByteArrayContent([1, 2, 3]), "files", fileName);

        return await client.PostAsync("/?handler=Drop", form, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Two printers on the team registration already made for this user, so the access path under
    /// test is the real one rather than a membership invented here.
    /// </summary>
    private (int first, int second) SeedPrinters(long userId, string firstName, string secondName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer first = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = firstName };
        Printer second = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = secondName };
        context.Printers.AddRange(first, second);
        context.SaveChanges();

        return (first.Id, second.Id);
    }

    private void SeedJobs(int printerId, long userId, int count)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        for (int i = 0; i < count; i++)
        {
            context.PrintJobs.Add(new PrintJob
            {
                TrackingId = Guid.NewGuid(),
                PrinterId = printerId,
                FileName = $"part-{i}.bgcode",
                QueuedByUserId = userId,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
                EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
        }

        context.SaveChanges();
    }

    private void SeedLiveState(int printerId,
                               float? chamber,
                               int? progress = null,
                               int? timeRemaining = null,
                               string? material = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printerId,
            NozzleTemperature = 25,
            ChamberTemperature = chamber,
            Progress = progress,
            TimeRemaining = timeRemaining,
            Material = material,
        });

        context.SaveChanges();
    }
}
