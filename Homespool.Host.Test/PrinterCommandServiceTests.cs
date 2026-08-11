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
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterCommandService"/> - the team-permission-checked entry point for sending a
/// command, and the first real consumer of <see cref="TeamMember.CanUse"/>.
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

    private static async Task<TeamMember> AddTeamAsync(HomespoolDbContext context, long userId, bool canRead, bool canUse, bool canManage)
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
                    CanRead = canRead,
                    CanUse = canUse,
                    CanManage = canManage,
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
    private static (PrinterConnectionRegistry registry, IPrinterConnectionActor actor) RegistryWithActor(int printerId, CommandSendResult result)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(result);

        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
        registry.Register(printerId, actor);

        return (registry, actor);
    }

    private static (PrinterConnectionRegistry registry, IPrinterConnectionActor actor) RegistryWithActor(int printerId, CommandSendOutcome outcome)
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

    private static CommandSendResult Answered(string? dataJson, Events eventType = Events.FileInfo, string? reason = null)
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
        PrinterCommandService service = new(new PrinterAccessService(context), registry);

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
            RegistryWithActor(printer.Id, Answered(null, Events.Rejected, "Won't execute the same command multiple times"));
        PrinterCommandService service = new(new PrinterAccessService(context), registry);

        // Act
        CommandOutcome<AskSomethingAnswer>? outcome =
            await service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        outcome!.EventType.Should().Be(Events.Rejected);
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
        PrinterCommandService service = new(new PrinterAccessService(context), registry);

        // Act
        Func<Task> ask = () => service.AskAsync(printer.Id, new AskSomething(), 1, CancellationToken.None);

        // Assert
        await ask.Should().ThrowAsync<CommandAnswerUnreadableException>()
                 .Where(e => e.WireName == "SEND_FILE_INFO" && e.PrinterId == printer.Id);
    }

    /// <summary>
    /// The permission gate is shared with <see cref="PrinterCommandService.SendCommandAsync"/> rather
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
        PrinterCommandService service = new(new PrinterAccessService(context), registry);

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
            RegistryWithActor(printer.Id, new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(Events.Finished, null)));
        PrinterCommandService service = new(new PrinterAccessService(context), registry);
        PausePrint command = new();

        // Act
        CommandOutcome? outcome = await service.SendCommandAsync(printer.Id, command, 1, CancellationToken.None);

        // Assert
        outcome.Should().NotBeNull("PAUSE_PRINT is answered - only unanswerable commands report null");
        outcome!.EventType.Should().Be(Events.Finished);
        await actor.Received(1).SendCommandAsync(command, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAccessDeniedWhenTheCallerCanReadButNotUse()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: false, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterCommandService service = new(new PrinterAccessService(context), RegistryWithActor(printer.Id, CommandSendOutcome.Completed).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

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

        PrinterCommandService service = new(new PrinterAccessService(context), RegistryWithActor(printer.Id, CommandSendOutcome.Completed).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsPrinterNotFoundForAnUnknownId()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        PrinterCommandService service = new(new PrinterAccessService(context), new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance));

        // Act
        Func<Task> act = () => service.SendCommandAsync(999, new PausePrint(), 1, CancellationToken.None);

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
        PrinterCommandService service = new(new PrinterAccessService(context), new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance));

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

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
        PrinterCommandService service = new(new PrinterAccessService(context), RegistryWithActor(printer.Id, CommandSendOutcome.NotConnected).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

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

        PrinterCommandService service = new(new PrinterAccessService(context), RegistryWithActor(printer.Id, CommandSendOutcome.AlreadyInFlight).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

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

        PrinterCommandService service = new(new PrinterAccessService(context), RegistryWithActor(printer.Id, CommandSendOutcome.ResponseTimedOut).registry);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<CommandResponseTimedOutException>();
    }
}
