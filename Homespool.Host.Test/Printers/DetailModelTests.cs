using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The printer detail page: the same "doesn't exist or can't read it" 404 rule
/// <see cref="PrinterQueryService.GetPrinterForUserAsync"/> follows, and that it correctly
/// surfaces live connection state.
/// </summary>
public sealed class DetailModelTests : IDisposable
{
    /// <summary>Shared and never poked here - the page only needs the service to construct.</summary>
    private static readonly QueueSignal QueueSignal = new();

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-detail-{Guid.NewGuid():N}.db");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<(DetailModel model, HSUser user, Team team, PrinterConnectionRegistry connectionRegistry)>
        NewModelAsync(HomespoolDbContext context)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("owner") { Email = "owner@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        Team team = context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        PrinterConnectionRegistry connectionRegistry = new(NullLogger<PrinterConnectionRegistry>.Instance);

        // The page reads its printer's queue, so it needs the real service - and that needs a file
        // store. Rooted in a temp directory that no test here ever writes to: these cases are about
        // the 404 rule and connection state, and an empty queue is the right backdrop for both.
        string storeRoot = Path.Combine(Path.GetTempPath(), "homespool-detail-" + Guid.NewGuid().ToString("N"));
        UserFileStore store = new(Options.Create(new PrintFileStorageOptions { Directory = storeRoot }),
                                  new HostEnvironmentAccessor(storeRoot),
                                  TimeProvider.System,
                                  NullLogger<UserFileStore>.Instance);

        PrinterAccessService access = new(context, NullLogger<PrinterAccessService>.Instance);
        PrintQueueService queueService = new(context, access,
                                             new PrintFileCatalog(store, context, NullLogger<PrintFileCatalog>.Instance),
                                             TimeProvider.System,
                                             QueueSignal);

        QueueSnapshotReader snapshots = new(context, connectionRegistry, TimeProvider.System);

        // One localiser, shared by the page and by the three text services it now holds, so a word
        // inside a sentence reads in the same language as the sentence.
        IStringLocalizer<SharedResource> localiser = TestLocaliser.Shared();

        DetailModel model = new(new PrinterQueryService(context, new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), new TeamCapabilityLookup(context), TimeProvider.System),
                                new PrinterRemovalService(context, access, snapshots, connectionRegistry,
                                                           Substitute.For<ITelemetryEviction>(),
                                                           NullLogger<PrinterRemovalService>.Instance),
                                queueService,

                                // Constructed rather than substituted: these tests are about the page, and a real one
                                // that never gets a connected printer simply refuses, which is the honest default here.
                                new PrinterPreheatService(commands: null!, snapshots, new ToolTargetReader(context)),

                                // Same reasoning as the preheat service above: real, with a null
                                // command service, so a guard that stops firing fails at the send
                                // rather than quietly pulling filament out of something.
                                new PrinterFilamentService(commands: null!, snapshots, new ToolTargetReader(context)),
                                new ToolTargetReader(context),
                                new PrintHistoryService(context, access, snapshots, new UserNameLookup(context)),
                                new UserNameLookup(context),
                                snapshots,
                                access,
                                new CameraAccessService(context, new TeamCapabilityLookup(context)),
                                new CameraDisplayNames(
                                    new LocalCameraDevices(
                                        NullLogger<LocalCameraDevices>.Instance,
                                        new UsbDeviceNames(NullLogger<UsbDeviceNames>.Instance))),
                                connectionRegistry,

                                // Null for the same reason the preheat service above takes one: these
                                // cases never press Set ready, and a page that would refuse anyway is
                                // the honest backdrop. The stop service is null on the same grounds -
                                // it would need a connected printer to do anything.
                                commands: null!,
                                stops: null!,
                                new PrinterStatusText(localiser),
                                new PrinterIntentText(localiser),
                                new RelativeTimeText(localiser),
                                TimeProvider.System,
                                localiser,
                                TestLocaliser.Errors(),
                                users)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user, team, connectionRegistry);
    }

    /// <summary>
    /// The refusal a person meets on the page matches the one the service raises.
    /// </summary>
    /// <remarks>
    /// Cooling is refused mid-print as well as heating, which reads as arbitrary unless the page says
    /// so - the button looks like a safety control and is not one. This asserts the refusal itself;
    /// the page's own wording is markup, and the two would drift apart silently if only the markup
    /// said it.
    /// </remarks>
    [Fact]
    public async Task CooldownIsRefusedWhileThePrinterIsPrinting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Printing,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostCooldownAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse("cooling a nozzle mid-print ruins the print without ending it");
        model.StatusMessage.Should().Contain("not busy");
    }

    /// <summary>
    /// Preheating is refused while the printer is printing, and says so on the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This guard is the only one there is.</b> Firmware understands a "forced" gcode frame meant
    /// to be the one accepted mid-print, with plain gcode refused, and does not implement the
    /// distinction (<c>connect.cpp</c>, with a TODO). It will retarget the nozzle in the middle of a
    /// print and ruin it without reporting anything wrong.
    /// </para>
    /// <para>
    /// The preheat service here is built with a null command service on purpose: if the guard ever
    /// stops firing, this fails loudly at the send rather than quietly heating something. The
    /// assertion is on the message, so an accidental pass cannot look like success.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PreheatIsRefusedWhileThePrinterIsPrinting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Printing,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostPreheatAsync(printer.Uuid, "PETG", CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse();
        model.StatusMessage.Should().Contain("Printing", "the answer names the state that refused it");
    }

    /// <summary>
    /// Unloading is refused mid-print, and the page says which state refused it.
    /// </summary>
    /// <remarks>
    /// The material is set, so a pass here cannot come from the printer having nothing loaded - it
    /// has to be the state guard. The filament service is built with a null command service on the
    /// same reasoning as the preheat one above: a guard that stopped firing would fail at the send
    /// rather than quietly pulling filament out of a running print.
    /// </remarks>
    [Fact]
    public async Task UnloadIsRefusedWhileThePrinterIsPrinting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Printing,
            Material = "PLA",
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostUnloadAsync(printer.Uuid, tool: null, CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse("retracting filament mid-print ruins the print");
        model.StatusMessage.Should().Contain("Printing", "the answer names the state that refused it");
    }

    /// <summary>
    /// The Unload control is offered only when the printer has named its filament.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>"---"</c> row is the one worth having.</b> It is what a printer with nothing loaded
    /// actually reports - the field is sent, carrying a sentinel - so a null check alone renders a
    /// button offering to unload a material called <c>---</c>, on a machine with nothing in it.
    /// </para>
    /// <para>
    /// Asserted on the page model rather than the markup, because what the view does with it is one
    /// <c>@if</c> and what decides it is this.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("PLA", true)]
    [InlineData("PETG", true)]
    [InlineData("---", false)]
    [InlineData(null, false)]
    public async Task TheUnloadControlFollowsWhetherThePrinterNamesItsFilament(string? material, bool offered)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, PrinterConnectionRegistry _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Idle,
            Material = material,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert. The printer is not connected in this fixture, so UnloadShown is false throughout -
        // LoadedMaterial is the half this decides, and the half the label reads from.
        model.LoadedMaterial.Should().Be(offered ? material : null);
    }

    /// <summary>
    /// Preheat and cool down are refused on a toolchanger with nothing picked, and this is the pair
    /// whose failure would be <em>partial</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>M140</c> is the bed and has no tool, so it lands regardless; <c>M104</c> declines.</b>
    /// Sending the pair would heat the bed and not the nozzle - or, cooling, leave the nozzle hot
    /// while the page reported both heaters off - with the printer answering the frame
    /// <c>Accepted</c> either way. <c>notes/toolchangers.md</c> §3d.
    /// </para>
    /// <para>
    /// <c>PreheatPartiallyAppliedException</c> was written for that shape and cannot reach it: it
    /// guards a second <em>command</em> failing after the first was acted on, and here the two lines
    /// are one frame.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HeaterControlsAreRefusedWhenNoToolIsPicked()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Idle,
            Material = "PLA",
            ActiveSlot = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        for (int tool = 1; tool <= 5; tool++)
        {
            context.PrinterTools.Add(new PrinterTool { PrinterId = printer.Id, ToolNumber = tool });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostPreheatAsync(printer.Uuid, "PETG", CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse("the bed would heat and the nozzle would not");
        model.StatusMessage.Should().Contain("No tool is picked");

        // Act - cooling has the same shape and the worse consequence
        await model.OnPostCooldownAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse("cooling would leave the nozzle hot and say otherwise");
    }

    /// <summary>
    /// The material tile keys on the fact, not on the hardware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row that decides the rule is the third one</b>: a toolchanger with PLA in two heads and
    /// nothing in the other two genuinely <em>is</em> a PLA printer, so the tile says something true.
    /// A rule keyed on tool count would have suppressed a fact for a reason unrelated to it.
    /// </para>
    /// <para>
    /// <b>Empty tools are excluded before the count rather than treated as disagreement</b> - without
    /// that, every toolchanger with one spare head would lose the tile.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(new[] { "PLA" }, "PLA")]
    [InlineData(new[] { "PLA", "PLA" }, "PLA")]
    [InlineData(new[] { "PLA", null, "PLA", null }, "PLA")]
    [InlineData(new[] { "PLA", "PETG" }, null)]
    [InlineData(new[] { "PLA", "PETG", "ABS", "PA" }, null)]
    [InlineData(new[] { (string?)null, null }, null)]
    public async Task TheMaterialTileShowsOnlyWhatEveryLoadedToolAgreesOn(string?[] materials, string? expected)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        PrinterLiveState live = new()
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Idle,
            Material = materials[0],
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        context.PrinterLiveStates.Add(live);

        // A slot block only exists above one tool, which is what firmware sends - so a single-material
        // case with one entry has to reach the same answer through the synthesised path instead.
        if (materials.Length > 1)
        {
            for (int slot = 1; slot <= materials.Length; slot++)
            {
                context.PrinterLiveSlotStates.Add(new PrinterLiveSlotState
                {
                    PrinterId = printer.Id,
                    SlotNumber = slot,
                    Material = materials[slot - 1],
                });
            }
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.SharedMaterial.Should().Be(expected);
        model.ToolTableShown.Should().Be(materials.Length > 1,
                                         "one tool is not a table - the tiles already say everything about it");
    }

    private static Printer NewPrinter(int teamId, string? name = null)
    {
        return new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Name = name,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task OnGetAsyncReturnsNotFoundForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, _, _) = await NewModelAsync(context);

        // Act
        IActionResult result = await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task OnGetAsyncReturnsNotFoundForAPrinterOnATeamTheCallerIsNotOn()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, _, _) = await NewModelAsync(context);

        Team someoneElsesTeam = context.AddDefaultTeam(2, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = NewPrinter(someoneElsesTeam.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IActionResult result = await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task OnGetAsyncPopulatesStatisticsForAnAccessiblePrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, name: "MK3.5");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IActionResult result = await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.Statistics.Printer.Id.Should().Be(printer.Id);
        model.Statistics.LiveState.Should().BeNull();
        model.Statistics.RecentSamples.Should().BeEmpty();
        model.Statistics.RecentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task OnGetAsyncReflectsWhetherThePrinterIsCurrentlyConnected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, PrinterConnectionRegistry connectionRegistry) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - not connected
        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.Connected.Should().BeFalse();

        // Act - now connected
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        connectionRegistry.Register(printer.Id, actor);

        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.Connected.Should().BeTrue();
    }

    // ---------- OnPostRemoveAsync ----------

    /// <summary>
    /// <b>The typed name is checked on the server.</b> A wrong one deletes nothing and says so.
    /// </summary>
    /// <remarks>
    /// The confirmation is the only thing standing between a stray click and a printer's entire
    /// history, so it cannot live in the browser: a confirmation the server never sees is a
    /// decoration. Asserting the printer survives, not just that the message is unhappy - a refusal
    /// that reported failure while deleting anyway would pass a message-only assertion.
    /// </remarks>
    [Fact]
    public async Task RemoveRefusesWhenTheTypedNameDoesNotMatch()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, name: "Workshop");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IActionResult result = await model.OnPostRemoveAsync(printer.Uuid, "workshp", CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse();
        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().BeNull("a refusal stays on this page");

        context.ChangeTracker.Clear();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>An empty box is a mismatch too, rather than a match against a printer with no name.</summary>
    [Fact]
    public async Task RemoveRefusesAnEmptyConfirmation()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, name: "Workshop");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostRemoveAsync(printer.Uuid, confirmation: null, CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeFalse();

        context.ChangeTracker.Clear();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// The right name deletes the printer and goes to the listing, since the page it was on is now
    /// about nothing.
    /// </summary>
    /// <remarks>
    /// The comparison ignores case deliberately - the point is to make somebody read the name, not to
    /// test their shift key - which is why this types it in lower case.
    /// </remarks>
    [Fact]
    public async Task RemoveWithTheRightNameRemovesThePrinterAndReturnsToTheListing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, name: "Workshop");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IActionResult result = await model.OnPostRemoveAsync(printer.Uuid, " workshop ", CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeTrue();
        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("Index");

        context.ChangeTracker.Clear();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A printer nobody ever named is confirmed by what the page calls it, which is its UUID - the
    /// same string the heading shows.
    /// </summary>
    [Fact]
    public async Task AnUnnamedPrinterIsConfirmedByTheNameThePageShows()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostRemoveAsync(printer.Uuid, printer.Uuid.ToString(), CancellationToken.None);

        // Assert
        model.StatusSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>A uuid the caller cannot read is a 404 here as much as on the GET.</summary>
    [Fact]
    public async Task RemoveReturnsNotFoundForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (DetailModel model, _, _, _) = await NewModelAsync(context);

        // Act
        IActionResult result = await model.OnPostRemoveAsync(Guid.NewGuid(), "anything", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
