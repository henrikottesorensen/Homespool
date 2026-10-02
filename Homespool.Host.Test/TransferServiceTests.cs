using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TransferService"/>'s mailbox: a printer's sends and settles one at a time, in order,
/// with nothing one of them does ending the loop for the rest.
/// </summary>
/// <remarks>
/// What a send and a settle each decide is driven through <see cref="QueueAdvancerTests"/>, which
/// reaches both through the queue. These are about the order they run in, against real SQLite and a
/// substituted actor whose answers the test releases.
/// </remarks>
public sealed class TransferServiceTests : IDisposable
{
    private const int PrinterId = 1;

    private const string FileName = "part.bgcode";

    /// <summary>The id the substituted actor's download command goes out under.</summary>
    private const uint DownloadCommandId = 4242;

    /// <summary>
    /// How long a wait on the mailbox may take before the test calls it stuck - a loop that has died
    /// would otherwise hang the run rather than fail it.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-transfers-{Guid.NewGuid():N}.db");
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "hs-transfers-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddYears(56));
    private readonly PrinterConnectionRegistry _registry;
    private readonly ServiceProvider _services;

    public TransferServiceTests()
    {
        _registry = new PrinterConnectionRegistry(_clock, NullLogger<PrinterConnectionRegistry>.Instance);

        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<PrinterAccessService>();
        services.AddSingleton(_registry);
        services.AddScoped<PrinterCommandService>();
        services.Configure<PrintFileStorageOptions>(options => options.Directory = _storeRoot);
        services.AddSingleton<IHostEnvironmentAccessor>(new HostEnvironmentAccessor(_storeRoot));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<UserFileStore>();
        services.AddScoped<PrintFileCatalog>();
        services.AddSingleton(new TransferOfferStore(_clock, TestOptions.Monitor(new PrusaConnectOptions()), NullLogger<TransferOfferStore>.Instance));
        services.AddSingleton<ITransferOffers>(sp => sp.GetRequiredService<TransferOfferStore>());
        services.AddSingleton<EncryptedTransferOffers>();
        services.AddSingleton(Options.Create(new PrusaConnectOptions()));
        services.AddScoped<PrintFileSender>();
        services.AddScoped<PrinterDriveNames>();
        services.AddScoped<PrinterDriveCopies>();
        services.AddLogging();
        services.AddTransfers();

        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();

        if (Directory.Exists(_storeRoot))
        {
            Directory.Delete(_storeRoot, recursive: true);
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A settle asked for while a send waits on the printer's answer runs after the send has recorded
    /// its attempt - so an end the printer reported before the answer arrived is matched, not passed
    /// over for good.
    /// </summary>
    [Fact]
    public async Task ASettleWaitsForTheSendAheadOfItToRecordItsAttempt()
    {
        // Arrange - a printer whose answer to the offer the test releases
        PrintFile file = await SeedAsync();
        TaskCompletionSource<CommandSendResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectAnsweringDownloadsWith(answer.Task, offered);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act - the send, which waits on the printer; its finish in the log; and a settle behind it
        Task<TransferResult> send = transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);
        await offered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferFinished);
        Task settle = transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        bool settledBeforeTheAnswer = settle.IsCompleted;

        answer.SetResult(new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.TransferInfo, null))
        {
            CommandId = DownloadCommandId,
        });
        await send;
        await settle;

        // Assert
        settledBeforeTheAnswer.Should().BeFalse("the send ahead of it had not finished");

        PrintFileOnPrinter row = await ReadRowAsync();

        row.ArrivedAt.Should().NotBeNull("the finish was read once the attempt it ends had been recorded");
        row.TransferCommandId.Should().BeNull();
    }

    /// <summary>
    /// A send whose caller gave up while it waited its turn is never offered: the printer is not asked
    /// for anything the caller will not hear the answer to.
    /// </summary>
    [Fact]
    public async Task ASendItsCallerGaveUpOnBeforeItsTurnIsNotOffered()
    {
        // Arrange - a first send holding the mailbox on the printer's answer
        PrintFile file = await SeedAsync();
        TaskCompletionSource<CommandSendResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPrinterConnectionActor actor = ConnectAnsweringDownloadsWith(answer.Task, offered);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        Task<TransferResult> first = transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);
        await offered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act - a second send, given up on while it waits, and then the first one's answer
        using CancellationTokenSource givenUp = new();
        Task<TransferResult> second = transfers.SendAsync(Request(file), givenUp.Token);
        await givenUp.CancelAsync();

        answer.SetResult(new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.TransferInfo, null))
        {
            CommandId = DownloadCommandId,
        });
        await first;
        await transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await second.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        DownloadsOffered(actor).Should().Be(1, "the second send was given up on before its turn");
    }

    /// <summary>
    /// A send that throws hands the exception to its caller and leaves the printer's mailbox running
    /// for the next one.
    /// </summary>
    [Fact]
    public async Task ASendThatThrowsDoesNotStopThePrintersMailbox()
    {
        // Arrange
        PrintFile file = await SeedAsync();
        ConnectAnsweringDownloadsWith(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                            new CommandOutcome(PrinterEventType.TransferInfo, null))
        {
            CommandId = DownloadCommandId,
        }));
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act
        Func<Task> failing = () => transfers.SendAsync(new TransferRequest(PrinterId, file.Id, Owner, new Throwing()),
                                                       TestContext.Current.CancellationToken)
                                            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Assert
        await failing.Should().ThrowAsync<InvalidOperationException>();

        TransferResult next = await transfers.SendAsync(Request(file), TestContext.Current.CancellationToken)
                                             .WaitAsync(Bound, TestContext.Current.CancellationToken);

        next.Sent.Should().NotBeNull("the mailbox carried on after the failure");
        (await ReadRowAsync()).TransferCommandId.Should().Be(DownloadCommandId);
    }

    /// <summary>
    /// The telemetry writer saying it saved a transfer's end is enough to settle it, with nobody - the
    /// queue included - asking.
    /// </summary>
    [Fact]
    public async Task AReportedEndIsSettledWithoutBeingAskedFor()
    {
        // Arrange - a direct send's attempt awaited, and its finish in the log
        PrintFile file = await SeedAsync();

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
            {
                PrinterId = PrinterId,
                PrintFileId = file.Id,
                DriveName = FileName,
                TransferCommandId = DownloadCommandId,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AddTransferEndAsync(PrinterEventType.TransferFinished);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act
        transfers.Saved(PrinterId, PrinterEventType.TransferFinished);

        // Assert - nothing to await, so the row is watched until it changes or the bound runs out
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Bound;
        PrintFileOnPrinter row = await ReadRowAsync();

        while (row.ArrivedAt is null && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            row = await ReadRowAsync();
        }

        row.ArrivedAt.Should().NotBeNull("the writer's word that the end was saved settles it");
    }

    /// <summary>A stopped service refuses a send rather than leaving its caller waiting.</summary>
    [Fact]
    public async Task AStoppedServiceRefusesASend()
    {
        // Arrange
        PrintFile file = await SeedAsync();
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act
        await transfers.StopAsync(TestContext.Current.CancellationToken);
        Func<Task> send = () => transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);

        // Assert
        await send.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The host may stop a service it has already disposed - a test host tearing down does - and
    /// stopping then is a no-op rather than a throw from a disposed token source.
    /// </summary>
    [Fact]
    public async Task StoppingADisposedServiceDoesNotThrow()
    {
        // Arrange - a mailbox in use, so there is something to stop
        await SeedAsync();
        TransferService transfers = _services.GetRequiredService<TransferService>();
        await transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        // Act
        transfers.Dispose();
        Func<Task> stop = () => transfers.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        await stop.Should().NotThrowAsync();
    }

    private static Caller Owner => Caller.Scoped(1, CapabilitySet.Everything);

    private static TransferRequest Request(PrintFile file)
    {
        return new TransferRequest(PrinterId, file.Id, Owner, new SendStoredFile());
    }

    private static int DownloadsOffered(IPrinterConnectionActor actor)
    {
        int offered = 0;

        foreach (NSubstitute.Core.ICall call in actor.ReceivedCalls())
        {
            if (call.GetArguments() is [StartConnectDownload or StartEncryptedDownload, ..])
            {
                offered++;
            }
        }

        return offered;
    }

    /// <summary>
    /// A connected printer that answers a download with <paramref name="answer"/>, signalling
    /// <paramref name="offered"/> when asked, and everything else with an empty <c>INFO</c>.
    /// </summary>
    private IPrinterConnectionActor ConnectAnsweringDownloadsWith(Task<CommandSendResult> answer, TaskCompletionSource? offered = null)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 if (call.Arg<ISendableCommand>() is not (StartConnectDownload or StartEncryptedDownload))
                 {
                     return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                  new CommandOutcome(PrinterEventType.Info, null)));
                 }

                 offered?.TrySetResult();

                 return answer;
             });

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>The download's answer and its end, as firmware reports them.</summary>
    private async Task AddTransferEndAsync(PrinterEventType ending)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        telemetry.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = PrinterId,
            Timestamp = _clock.GetUtcNow(),
            EventType = PrinterEventType.TransferInfo,
            CommandId = DownloadCommandId,
            Payload = $"{{\"path\":\"/usb/{FileName}\",\"start_cmd_id\":{DownloadCommandId},\"type\":\"FROM_CONNECT\"}}",
        });
        telemetry.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = PrinterId,
            Timestamp = _clock.GetUtcNow(),
            EventType = ending,
            Payload = $"{{\"start_cmd_id\":{DownloadCommandId}}}",
        });

        await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PrintFileOnPrinter> ReadRowAsync()
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintFilesOnPrinters
                          .AsNoTracking()
                          .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A user with a file on disk, on a team with a printer.</summary>
    private async Task<PrintFile> SeedAsync()
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        string directory = Path.Combine(_storeRoot, "1-owner");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, FileName), "G28 ; home\n", TestContext.Current.CancellationToken);

        const string email = "owner@example.com";
        context.Users.Add(new HSUser(email)
        {
            Id = 1,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        });

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = 1,
            Capabilities = TestMemberships.Literal(CapabilityPresets.Operator),
        });
        context.Printers.Add(new Printer { Id = PrinterId, Uuid = Guid.NewGuid(), TeamId = team.Id });

        PrintFile file = new() { UserId = 1, Name = FileName, Size = 11, UploadedAt = _clock.GetUtcNow() };
        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return file;
    }

    /// <summary>A sender with nothing to decide: the file as the store has it.</summary>
    private sealed class SendStoredFile : TransferPolicy
    {
        public override Task<StoredFile?> FindFileAsync(TransferContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(context.Services.GetRequiredService<PrintFileCatalog>()
                                          .FindForPrinting(context.PrintFile.UserId, context.PrintFile.Name));
        }
    }

    /// <summary>A sender whose first decision throws.</summary>
    private sealed class Throwing : TransferPolicy
    {
        public override Task<StoredFile?> FindFileAsync(TransferContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("a policy that throws");
        }
    }
}
