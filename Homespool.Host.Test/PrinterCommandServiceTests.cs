using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterCommandService"/> - the team-permission-checked entry point for sending a
/// command, and the first real consumer of <see cref="TeamMember.Capabilities"/>.
/// </summary>
/// <remarks>
/// Run against real SQLite, matching <c>PrinterQueryServiceTests</c>.
/// </remarks>
public sealed class PrinterCommandServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printercommand-{Guid.NewGuid():N}.db");

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

    private static async Task<TeamMember> AddTeamAsync(HomespoolDbContext context,
                                                       long userId,
                                                       bool canRead,
                                                       bool canUse,
                                                       bool canManage)
    {
        Team team = new()
        {
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Members =
            {
                new TeamMember
                {
                    UserId = userId,
                    Capabilities = TestMemberships.Graded(canRead, canUse, canManage),
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team.Members.Single();
    }

    private static async Task<Printer> AddPrinterAsync(HomespoolDbContext context, int teamId)
    {
        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    /// <summary>
    /// A registry holding one substitute actor forced to a deterministic outcome, instead of driving
    /// a real connection and loop - these tests are about the permission gate and the
    /// outcome-to-exception mapping, not the wire.
    /// </summary>
    private static (PrinterConnectionRegistry registry, IPrinterConnectionActor actor) RegistryWithActor(
        int printerId,
        CommandSendResult result)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(result);

        // The intent overload goes through the link rather than the wire command, so a substitute
        // answering only the latter returns null and the service fails on the result rather than on
        // the permission the test is about.
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(result);

        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
        registry.Register(printerId, actor);

        return (registry, actor);
    }

    private static (PrinterConnectionRegistry registry, IPrinterConnectionActor actor) RegistryWithActor(
        int printerId,
        CommandSendOutcome outcome)
    {
        return RegistryWithActor(printerId, new CommandSendResult(outcome, null));
    }

    /// <summary>
    /// A question-asking command, standing in for <c>SendFileInfo</c> until that one is sendable.
    /// Deliberately local to the tests: what is under test is that <see cref="ISendableCommand{T}"/>
    /// carries the answer's type through, not any particular command's wire shape.
    /// </summary>
    private sealed class AskSomething : ISendableCommand<AskSomethingAnswer>
    {
        public string WireName => "SEND_FILE_INFO";
    }

    // CA1812 flags this as never instantiated, which is true at compile time: System.Text.Json only
    // ever builds it by reflection, which is the whole point of the test.
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json when deserializing a command's answer.")]
    private sealed class AskSomethingAnswer
    {
        [JsonPropertyName("file_count")]
        public int FileCount { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    private static CommandSendResult Answered(string? dataJson, PrinterEventType eventType = PrinterEventType.FileInfo, string? reason = null)
    {
        return new(CommandSendOutcome.Completed,
                   new CommandOutcome(eventType, reason),
                   dataJson is null ? null : JsonSerializer.Deserialize<JsonElement>(dataJson));
    }

    [Fact]
    public async Task AskAsyncParsesTheAnswerIntoTheShapeTheCommandDeclared()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        (PrinterConnectionRegistry registry, _) =
            RegistryWithActor(printer.Id, Answered("""{"file_count":3,"path":"/usb","type":"FOLDER"}"""));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);

        // Act
        CommandOutcome<AskSomethingAnswer>? outcome =
            await service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        outcome.Should().NotBeNull();
        outcome!.Answer.Should().NotBeNull();
        outcome.Answer!.FileCount.Should().Be(3);
        outcome.Answer.Path.Should().Be("/usb");
    }

    /// <summary>
    /// A refusal is a real answer, and arrives with no payload - so a null
    /// <see cref="CommandOutcome{T}.Answer"/> must not be read as failure.
    /// </summary>
    [Fact]
    public async Task AskAsyncGivesTheVerdictAndNoAnswerWhenThePrinterRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        (PrinterConnectionRegistry registry, _) =
            RegistryWithActor(printer.Id, Answered(null, PrinterEventType.Rejected, "Won't execute the same command multiple times"));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);

        // Act
        CommandOutcome<AskSomethingAnswer>? outcome =
            await service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        outcome!.EventType.Should().Be(PrinterEventType.Rejected);
        outcome.Reason.Should().Be("Won't execute the same command multiple times");
        outcome.Answer.Should().BeNull();
    }

    /// <summary>
    /// A payload whose field types have moved throws, rather than arriving as a null answer that
    /// would be indistinguishable from the printer refusing the command.
    /// </summary>
    [Fact]
    public async Task AskAsyncThrowsWhenTheAnswerWillNotParse()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        (PrinterConnectionRegistry registry, _) =
            RegistryWithActor(printer.Id, Answered("""{"file_count":"three","path":"/usb"}"""));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);

        // Act
        Func<Task> ask = () => service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        await ask.Should().ThrowAsync<CommandAnswerUnreadableException>()
                 .Where(e => e.WireName == "SEND_FILE_INFO" && e.PrinterId == printer.Id);
    }

    /// <summary>
    /// The permission gate is shared with <see cref="PrinterCommandService.SendCommandAsync(int, ISendableCommand, long, System.Threading.CancellationToken)"/> rather
    /// than reimplemented - which is exactly what extracting the common half could have broken
    /// silently, since nothing else asks a question yet.
    /// </summary>
    [Fact]
    public async Task AskAsyncEnforcesCanUseLikeSendCommandAsyncDoes()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: false, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        (PrinterConnectionRegistry registry, _) = RegistryWithActor(printer.Id, Answered("""{"file_count":1}"""));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);

        // Act
        Func<Task> ask = () => service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        await ask.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncReturnsTheOutcomeWhenTheCallerCanUse()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        (PrinterConnectionRegistry registry, IPrinterConnectionActor actor) =
            RegistryWithActor(
                printer.Id, new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.Finished, null)));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);
        PrusaConnect.Commands.PausePrint command = new();

        // Act
        CommandOutcome? outcome = await service.SendCommandAsync(printer.Id, command, 1, CancellationToken.None);

        // Assert
        outcome.Should().NotBeNull("PAUSE_PRINT is answered - only unanswerable commands report null");
        outcome!.EventType.Should().Be(PrinterEventType.Finished);
        await actor.Received(1).SendCommandAsync(command, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <b>The command decides what it needs, not the caller.</b> Somebody holding <c>Print</c> and no
    /// more can send <c>START_PRINT</c> - which is what lets <c>QueueAdvancer</c> run their queued
    /// work as them - and is refused <c>PAUSE_PRINT</c>, which steers the machine.
    /// </summary>
    /// <remarks>
    /// Both halves are in one test on purpose: separately, either passes for the wrong reason. A gate
    /// hardcoded to <c>ControlPrinter</c> - which is what this was before capabilities - fails the
    /// first assertion; a gate that ignored the requirement entirely fails the second.
    /// </remarks>
    [Fact]
    public async Task ACommandsOwnRequirementDecidesTheGateRatherThanTheCaller()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(TestMemberships.With(team.Id, 1, Capability.ViewPrinter, Capability.Print));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = await AddPrinterAsync(context, team.Id);

        (PrinterConnectionRegistry registry, IPrinterConnectionActor _) =
            RegistryWithActor(
                printer.Id,
                new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.Finished, null)));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            registry);

        // Act
        CommandOutcome? outcome = await service.SendCommandAsync(
            printer.Id, new Printing.StartPrint("/usb/one.bgcode"), 1, CancellationToken.None);

        Func<Task> pausing = () =>
            service.SendCommandAsync(printer.Id, new Printing.PausePrint(), 1, CancellationToken.None);

        // Assert
        outcome.Should().NotBeNull("StartPrint requires Print, which this membership holds");

        await pausing.Should()
                     .ThrowAsync<TeamAccessDeniedException>("PausePrint requires ControlPrinter, which it does not");
    }

    /// <summary>
    /// <b>Readying is <see cref="Capability.Print"/>, not <see cref="Capability.ControlPrinter"/>.</b>
    /// The queue gates on the printer being ready and readying is per print, so a contributor who
    /// could queue work but not ready the bed could never start it - and whoever just cleared the
    /// sheet is the best-informed person to assert it is clear.
    /// </summary>
    /// <remarks>
    /// Mutation check: remove the <c>RequiredCapability</c> override from either intent and it falls
    /// back to <c>ControlPrinter</c>, which this membership does not hold, and this test fails.
    /// <see cref="Printing.SetPrinterIdle"/> is asserted alongside precisely because it did
    /// <i>not</i> move - it is a machine-state act rather than a job-shaped one.
    /// </remarks>
    [Fact]
    public async Task ReadyingAPrinterNeedsPrintRatherThanControlPrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        Team team = new() { CreatedBy = 1, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A contributor: may put work on the printer, may not steer it.
        context.TeamMembers.Add(TestMemberships.With(team.Id, 1, [.. CapabilityPresets.Contributor]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = await AddPrinterAsync(context, team.Id);

        (PrinterConnectionRegistry registry, IPrinterConnectionActor _) =
            RegistryWithActor(
                printer.Id,
                new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.Finished, null)));
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            registry);

        // Act
        CommandOutcome? readying = await service.SendCommandAsync(
            printer.Id, new Printing.SetPrinterReady(), 1, CancellationToken.None);

        CommandOutcome? unreadying = await service.SendCommandAsync(
            printer.Id, new Printing.CancelPrinterReady(), 1, CancellationToken.None);

        Func<Task> idling = () =>
            service.SendCommandAsync(printer.Id, new Printing.SetPrinterIdle(), 1, CancellationToken.None);

        // Assert
        readying.Should().NotBeNull("a contributor must be able to ready the bed for their own print");
        unreadying.Should().NotBeNull("withdrawing an assertion they were entitled to make");

        await idling.Should()
                    .ThrowAsync<TeamAccessDeniedException>(
                        "SetPrinterIdle is a machine-state act and stayed at ControlPrinter");
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAccessDeniedWhenTheCallerCanReadButNotUse()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: false, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            RegistryWithActor(printer.Id, CommandSendOutcome.Completed).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAccessDeniedWhenTheCallerIsNotOnTheTeamAtAll()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            RegistryWithActor(printer.Id, CommandSendOutcome.Completed).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsPrinterNotFoundForAnUnknownId()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance));

        // Act
        Func<Task> act = () => service.SendCommandAsync(999, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterNotFoundException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsNotConnectedWhenNoActorIsRegisteredForThePrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // An empty registry: the printer has no live connection at all.
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance));

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterNotConnectedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsNotConnectedWhenTheActorReportsNotConnected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // The other path to the same exception: an actor exists but its connection is gone (or went
        // mid-send) - the actor reports it as an outcome rather than throwing.
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            RegistryWithActor(printer.Id, CommandSendOutcome.NotConnected).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterNotConnectedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAlreadyInFlightWhenTheActorReportsAlreadyInFlight()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            RegistryWithActor(printer.Id, CommandSendOutcome.AlreadyInFlight).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<CommandAlreadyInFlightException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsTimedOutWhenTheActorReportsTimedOut()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                            RegistryWithActor(printer.Id, CommandSendOutcome.ResponseTimedOut).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<CommandResponseTimedOutException>();
    }

    /// <summary>
    /// The protocol-neutral path: an intent reaches a link that is <i>not</i> the Prusa actor and
    /// is sent through it untranslated by the service - the link owns translation, so the service
    /// never learns which protocol it spoke to. This is the seam a second protocol plugs into.
    /// </summary>
    [Fact]
    public async Task AnIntentReachesANonPrusaLinkThroughTheNeutralPath()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        IPrinterLink link = Substitute.For<IPrinterLink>();
        link.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
            .Returns(new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.Finished, null)));

        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
        registry.Register(printer.Id, link);
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);
        Printing.PausePrint intent = new();

        // Act
        CommandOutcome? outcome = await service.SendCommandAsync(printer.Id, intent, 1, CancellationToken.None);

        // Assert
        outcome!.EventType.Should().Be(PrinterEventType.Finished);
        await link.Received(1).SendAsync(intent, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The honest edge of the seam: a wire-typed Prusa command has no neutral shape yet, so a link
    /// that is not the Prusa actor refuses it with a specific exception rather than pretending -
    /// distinct from "not connected", because the printer <i>is</i> connected.
    /// </summary>
    [Fact]
    public async Task AWireTypedCommandToANonPrusaLinkIsRefusedAsUnsupported()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
        registry.Register(printer.Id, Substitute.For<IPrinterLink>());
        PrinterCommandService service = new(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PrusaConnect.Commands.PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterProtocolUnsupportedException>();
    }
}
