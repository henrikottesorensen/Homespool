using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The guards standing between the Unload button and a ruined print.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exhaustive over <see cref="PrinterStatus"/> rather than a couple of cases, and deliberately
/// so.</b> Firmware understands a "forced" gcode frame meant to be the one accepted mid-print and
/// <em>does not implement the distinction</em> (<c>connect.cpp:490</c>, with a TODO), so it will
/// retract filament in the middle of a print and report nothing wrong. This check is the whole of
/// the protection, and a state nobody thought to test is exactly the way it fails.
/// </para>
/// <para>
/// <b>The command service is null throughout, and that is the mechanism.</b> Nothing here needs a
/// printer to talk to: an allowed state falls through to the material check and raises
/// <see cref="FilamentTypeUnknownException"/>, which is a positive signal that the state guard let it
/// past. A guard that stopped firing would reach the send and fail on the null instead, so a broken
/// guard cannot look like a pass.
/// </para>
/// </remarks>
public sealed class PrinterFilamentServiceTests : IDisposable
{
    private const int PrinterId = 1;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-filament-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Every state the shared rule does not allow refuses, naming itself.
    /// </summary>
    /// <remarks>
    /// The material is set throughout, so nothing here can pass for the wrong reason - a refusal
    /// means the state refused it, not that the printer had nothing loaded.
    /// </remarks>
    [Theory]
    [InlineData(PrinterStatus.Undefined)]
    [InlineData(PrinterStatus.Unknown)]
    [InlineData(PrinterStatus.Busy)]
    [InlineData(PrinterStatus.Printing)]
    [InlineData(PrinterStatus.Paused)]
    [InlineData(PrinterStatus.Error)]
    [InlineData(PrinterStatus.Attention)]
    [InlineData(PrinterStatus.Manipulating)]
    [InlineData(PrinterStatus.Offline)]
    public async Task ABusyPrinterRefusesToUnload(PrinterStatus status)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, status, material: "PLA");

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        (await unload.Should().ThrowAsync<PrinterBusyException>()).Which.Status.Should().Be(status);
    }

    /// <summary>
    /// <c>Attention</c> is refused, and it is the one worth naming on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A filament runout puts a printer here, which is exactly when somebody wants to unload -
    /// and it is also mid-print, which is the case the guard exists for.</b> Decided out rather than
    /// inherited from preheating: <c>Attention</c> is one value covering a crash stop, a "remove the
    /// print" prompt and an MMU error as well as a runout, so there is no reading of it that
    /// separates the safe case from the ruinous one.
    /// </para>
    /// <para>
    /// The runout case is also already served, at the machine, by firmware's own filament-change
    /// flow. The remote equivalent of that is "resume with new filament", which is a different
    /// feature from this one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AttentionIsRefusedEvenThoughARunoutLandsThere()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Attention, material: "PLA");

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<PrinterBusyException>(
            "a runout is one of several things Attention means, and the others are mid-print");
    }

    /// <summary>
    /// Every state the shared rule allows gets past the state guard.
    /// </summary>
    /// <remarks>
    /// Reaching <see cref="FilamentTypeUnknownException"/> is the assertion: it is raised after the
    /// state check and before anything is sent, so it can only be reached by a printer whose state
    /// was accepted.
    /// </remarks>
    [Theory]
    [InlineData(PrinterStatus.Idle)]
    [InlineData(PrinterStatus.Ready)]
    [InlineData(PrinterStatus.Finished)]
    [InlineData(PrinterStatus.Stopped)]
    public async Task AQuietPrinterGetsPastTheStateGuard(PrinterStatus status)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, status, material: null);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<FilamentTypeUnknownException>(
            "reaching the material check means the state was accepted");
    }

    /// <summary>
    /// Every member of the enum is covered by one of the two theories above.
    /// </summary>
    /// <remarks>
    /// <b>The point is the member added next year.</b> A new <see cref="PrinterStatus"/> lands in
    /// neither list, and without this it would simply go untested - which for a denylist would mean
    /// failing open. The allow-set means it fails closed instead, but silently, and a guard nobody
    /// has decided about is still a decision nobody made.
    /// </remarks>
    [Fact]
    public void EveryStatusIsDecidedOneWayOrTheOther()
    {
        IEnumerable<PrinterStatus> covered =
            Cases(nameof(ABusyPrinterRefusesToUnload)).Concat(Cases(nameof(AQuietPrinterGetsPastTheStateGuard)));

        Enum.GetValues<PrinterStatus>().Should().BeSubsetOf(covered,
            "a status in neither theory is a state nobody decided about");
    }

    /// <summary>
    /// A Ready printer with work queued is refused, and this is unloading's own rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>QueueRules.IsAvailable</c> is <c>status == Ready</c></b>, so the loop starts the head
    /// within about a second of finding one. Unloading here means a print that runs to completion
    /// extruding nothing, with no refusal anywhere - the failure is silent, which is what makes it
    /// worth a guard rather than a warning.
    /// </para>
    /// <para>
    /// Preheating needs no equivalent, which is why this is not in the shared rule: a print sets its
    /// own temperatures on the way in, and there is no comparable recovery for filament that is not
    /// in the machine.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReadyPrinterWithWorkQueuedIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Ready, material: "PLA");
        await QueueAPrintAsync(context);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<PrinterHasQueuedWorkException>();
    }

    /// <summary>
    /// A Ready printer with an empty queue is not refused - the ordinary "just finished, now change
    /// the filament" case, which is most of why Ready is allowed at all.
    /// </summary>
    [Fact]
    public async Task AReadyPrinterWithAnEmptyQueueIsAllowed()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Ready, material: null);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<FilamentTypeUnknownException>(
            "an empty queue means Ready carries no instruction to start anything");
    }

    /// <summary>
    /// The queue check is Ready's alone: an Idle printer with work queued is not refused, because
    /// the loop will not start anything until somebody marks it ready.
    /// </summary>
    [Fact]
    public async Task AnIdlePrinterWithWorkQueuedIsStillAllowed()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Idle, material: null);
        await QueueAPrintAsync(context);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<FilamentTypeUnknownException>(
            "an Idle printer is not going to start the queue on its own");
    }

    /// <summary>
    /// Firmware's "no filament" sentinel is refused exactly as a missing value is.
    /// </summary>
    /// <remarks>
    /// <b>The case a null check alone gets wrong.</b> <c>"---"</c> arrives in the ordinary
    /// <c>material</c> field - <c>render.cpp</c>'s guard is <c>!material.empty()</c>, which that
    /// string passes - so a printer with nothing loaded reports a material rather than omitting one.
    /// Sending <c>M702 W0</c> to it opens a dialog on the panel and blocks there.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData(" --- ")]
    public async Task APrinterThatNamesNoFilamentIsRefused(string? material)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Idle, material);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<FilamentTypeUnknownException>(
            "firmware would stop at a preheat dialog nobody is standing at");
    }

    /// <summary>A printer with no live state at all reads as Unknown, not as permission.</summary>
    [Fact]
    public async Task APrinterThatHasNeverReportedIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, status: null, material: null);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<PrinterBusyException>();
    }

    /// <summary>
    /// A toolchanger with nothing picked is refused, and nothing is sent.
    /// </summary>
    /// <remarks>
    /// <b>The refusal has to come before the frame, not instead of believing it.</b> Firmware would
    /// answer <c>M702</c> <c>Accepted</c> and then return having done nothing, reporting only to the
    /// serial console - so a page that sent it would report an unload that never happened. On a
    /// toolchanger this is the resting state: <c>M702</c> itself docks to <c>NoTool</c> when it
    /// finishes.
    /// </remarks>
    [Fact]
    public async Task AToolchangerWithNothingPickedIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Idle, material: "PLA", activeSlot: 0, toolCount: 5);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<NoToolPickedException>();
    }

    /// <summary>
    /// A toolchanger with a tool picked is allowed, because the command reaches that tool correctly.
    /// </summary>
    /// <remarks>
    /// Reaching the material check is the assertion, as elsewhere here: it sits after the tool gate
    /// and before anything is sent.
    /// </remarks>
    [Fact]
    public async Task AToolchangerWithAToolPickedGetsPastTheToolGate()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Idle, material: null, activeSlot: 3, toolCount: 5);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<FilamentTypeUnknownException>(
            "a picked tool is a target, so the tool gate has nothing to refuse");
    }

    /// <summary>
    /// A multi-tool printer that has not reported a slot block yet is refused rather than assumed
    /// safe.
    /// </summary>
    [Fact]
    public async Task AMultiToolPrinterThatHasNotSaidWhichToolIsPickedIsRefused()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, PrinterStatus.Idle, material: "PLA", activeSlot: null, toolCount: 5);

        PrinterFilamentService service = NewService(context);

        Func<Task> unload = () => service.UnloadAsync(PrinterId, Caller.Unscoped(1), CancellationToken.None);

        await unload.Should().ThrowAsync<NoToolPickedException>(
            "several tools and no word on which is live is not a printer to act on");
    }

    /// <summary>The states a theory on this class declares, read from its own attributes.</summary>
    /// <remarks>
    /// Read rather than restated, so the coverage assertion cannot pass against a stale second copy
    /// of the lists it is checking.
    /// </remarks>
    private static IEnumerable<PrinterStatus> Cases(string testName)
    {
        return typeof(PrinterFilamentServiceTests)
               .GetMethod(testName)!
               .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
               .Cast<InlineDataAttribute>()
               .Select(attribute => (PrinterStatus)attribute.Data[0]!);
    }

    private static PrinterFilamentService NewService(HomespoolDbContext context)
    {
        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);

        return new PrinterFilamentService(commands: null!,
                                          new QueueSnapshotReader(context, registry, TimeProvider.System),
                                          new ToolTargetReader(context),
                                          context);
    }

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

    private static async Task SeedAsync(HomespoolDbContext context,
                                        PrinterStatus? status,
                                        string? material,
                                        int? activeSlot = null,
                                        int toolCount = 1)
    {
        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(new Printer
        {
            Id = PrinterId,
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (status is { } reported)
        {
            context.PrinterLiveStates.Add(new PrinterLiveState
            {
                PrinterId = PrinterId,
                ActiveSlot = activeSlot,
                Status = reported,
                Material = material,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
        }

        for (int tool = 1; tool <= toolCount; tool++)
        {
            context.PrinterTools.Add(new PrinterTool { PrinterId = PrinterId, ToolNumber = tool });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task QueueAPrintAsync(HomespoolDbContext context)
    {
        context.Users.Add(new HSUser
        {
            Id = 1,
            UserName = "queuer",
            NormalizedUserName = "QUEUER",
            Email = "queuer@example.com",
            NormalizedEmail = "QUEUER@EXAMPLE.COM",
        });

        PrintFile file = new()
        {
            UserId = 1,
            Name = "queued.bgcode",
            Size = 1024,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TrackingId = Guid.NewGuid(),
            Position = 0,
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
