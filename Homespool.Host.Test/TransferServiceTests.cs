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
using Homespool.Host.Exceptions;
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
    /// over for good. The queue's sends and a person's alike.
    /// </summary>
    /// <param name="direct">Whether the send is a person's, through <see cref="TransferService.SendDirectAsync"/>.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASettleWaitsForTheSendAheadOfItToRecordItsAttempt(bool direct)
    {
        // Arrange - a printer whose answer to the offer the test releases
        PrintFile file = await SeedAsync();
        TaskCompletionSource<CommandSendResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectAnsweringDownloadsWith(answer.Task, offered);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act - the send, which waits on the printer; its finish in the log; and a settle behind it
        Task send = direct ?
            await SendDirectAsync(transfers, file) :
            transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);
        await offered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferFinished);
        Task settle = transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        // Given every chance to run first: a settle that did not wait would be done well within this,
        // and one that waits cannot be, so the wait costs a correct run nothing but the time.
        await Task.WhenAny(settle, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
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
    /// A second send to a printer while one is under way is refused at once, as a second command to
    /// a busy printer always was - not queued to wait out the first one's answer - and the next send
    /// after the first has ended is let through.
    /// </summary>
    [Fact]
    public async Task ASecondSendWhileOneIsUnderWayIsRefusedAtOnce()
    {
        // Arrange - a first send holding the printer's mailbox on its answer
        PrintFile file = await SeedAsync();
        TaskCompletionSource<CommandSendResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPrinterConnectionActor actor = ConnectAnsweringDownloadsWith(answer.Task, offered);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        Task<TransferResult> first = transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);
        await offered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Act
        Func<Task> second = () => transfers.SendAsync(Request(file), TestContext.Current.CancellationToken)
                                           .WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Assert - refused, not left waiting on the first one's answer
        await second.Should().ThrowAsync<CommandAlreadyInFlightException>();

        answer.SetResult(TakenAs(DownloadCommandId));
        await first.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await transfers.SendAsync(Request(file), TestContext.Current.CancellationToken)
                       .WaitAsync(Bound, TestContext.Current.CancellationToken);
        DownloadsOffered(actor).Should().Be(2, "the refused send offered nothing, and the one after the first was let through");
    }

    /// <summary>
    /// A send whose caller had given up by the time its turn came is never offered: the printer is not
    /// asked for anything nobody will hear the answer to.
    /// </summary>
    [Fact]
    public async Task ASendWhoseCallerHasGivenUpIsNotOffered()
    {
        // Arrange
        PrintFile file = await SeedAsync();
        IPrinterConnectionActor actor = ConnectAnsweringDownloadsWith(Task.FromResult(TakenAs(DownloadCommandId)));
        TransferService transfers = _services.GetRequiredService<TransferService>();
        using CancellationTokenSource givenUp = new();
        await givenUp.CancelAsync();

        // Act
        Func<Task> send = () => transfers.SendAsync(Request(file), givenUp.Token);

        // Assert
        await send.Should().ThrowAsync<OperationCanceledException>();
        await transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);
        DownloadsOffered(actor).Should().Be(0);

        await transfers.SendAsync(Request(file), TestContext.Current.CancellationToken)
                       .WaitAsync(Bound, TestContext.Current.CancellationToken);
        DownloadsOffered(actor).Should().Be(1, "the given-up send no longer counted as one under way");
    }

    /// <summary>
    /// The queue's settle comes back at once, settling nothing, while a send is under way - its pass
    /// must not wait out a silent printer's answer - and settles as usual once the send has ended.
    /// </summary>
    [Fact]
    public async Task TheQueuesSettleDoesNotWaitBehindASend()
    {
        // Arrange - a send holding the printer's mailbox on its answer
        PrintFile file = await SeedAsync();
        TaskCompletionSource<CommandSendResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectAnsweringDownloadsWith(answer.Task, offered);
        TransferService transfers = _services.GetRequiredService<TransferService>();

        Task<TransferResult> send = transfers.SendAsync(Request(file), TestContext.Current.CancellationToken);
        await offered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Act
        bool settledWhileSending = await transfers.SettleUnlessSendingAsync(PrinterId, TestContext.Current.CancellationToken)
                                                  .WaitAsync(Bound, TestContext.Current.CancellationToken);

        answer.SetResult(TakenAs(DownloadCommandId));
        await send.WaitAsync(Bound, TestContext.Current.CancellationToken);

        bool settledAfter = await transfers.SettleUnlessSendingAsync(PrinterId, TestContext.Current.CancellationToken)
                                           .WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Assert
        settledWhileSending.Should().BeFalse("a send was under way");
        settledAfter.Should().BeTrue();
    }

    /// <summary>
    /// A pass that starts at the very moment a send's caller is told how it ended - as the next pass
    /// of a queue does - finds the printer free to settle, never still sending: the send is let go of
    /// before its caller hears of it. The caller here runs inline in the telling, which puts its next
    /// pass exactly in the gap a later release would leave.
    /// </summary>
    [Fact]
    public async Task ASettleStartedAsASendEndsFindsThePrinterFree()
    {
        // Arrange
        PrintFile file = await SeedAsync();
        ConnectAnsweringDownloadsWith(Task.FromResult(TakenAs(DownloadCommandId)));
        TransferService transfers = _services.GetRequiredService<TransferService>();
        TaskCompletionSource<Task<bool>> nextPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        Task caller = Task.Run(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

            // No token that could be cancelled, so the caller waits on the send's own completion - not on
            // a wrapper that the pool completes later - and is run by the mailbox's thread as it is told.
#pragma warning disable xUnit1051 // Deliberately not the test's token: see above.
            await transfers.SendAsync(Request(file), CancellationToken.None);
#pragma warning restore xUnit1051

            // Runs inside the send's completion, on the mailbox's own thread.
            nextPass.SetResult(transfers.SettleUnlessSendingAsync(PrinterId, TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken);

        await caller.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Task<bool> pass = await nextPass.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Assert
        (await pass.WaitAsync(Bound, TestContext.Current.CancellationToken)).Should().BeTrue("the send was over by the time its caller was told");
    }

    /// <summary>
    /// A person's send of a file the queue is transferring, refused or falling short, leaves the
    /// queue's attempt as it was: its stamp, and the command whose end it awaits.
    /// </summary>
    /// <param name="refused">Whether the printer refused the offer, rather than the send not reaching it.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADirectSendThatDoesNotTakeLeavesTheQueuesAttemptAlone(bool refused)
    {
        // Arrange - the queue's attempt in flight
        PrintFile file = await SeedAsync();
        DateTimeOffset started = _clock.GetUtcNow();
        await AddQueuedAttemptAsync(file, started);

        ConnectAnsweringDownloadsWith(Task.FromResult(refused ?
                                                          RefusedAsBusy(DownloadCommandId + 1) :
                                                          new CommandSendResult(CommandSendOutcome.NotConnected, null)));
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act
        Func<Task> send = async () => await await SendDirectAsync(transfers, file);

        if (refused)
        {
            await send();
        }
        else
        {
            await send.Should().ThrowAsync<PrinterNotConnectedException>();
        }

        // Assert
        PrintFileOnPrinter row = await ReadRowAsync();

        row.TransferStartedAt.Should().Be(started, "the queue's transfer is still the one running");
        row.TransferCommandId.Should().Be(DownloadCommandId, "its end is still the one awaited");
    }

    /// <summary>
    /// A person's send the printer takes replaces whatever the queue had in flight - the printer has one
    /// transfer slot - so its end is awaited instead, and its abort is not counted against the queued
    /// entry.
    /// </summary>
    [Fact]
    public async Task ADirectSendThePrinterTakesReplacesTheQueuesAttempt()
    {
        // Arrange - a stale attempt of the queue's, and its entry
        PrintFile file = await SeedAsync();
        await AddQueuedAttemptAsync(file, _clock.GetUtcNow());
        ConnectAnsweringDownloadsWith(Task.FromResult(TakenAs(DownloadCommandId + 1)));
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act - the direct send taken, then given up by the printer
        await await SendDirectAsync(transfers, file);
        PrintFileOnPrinter taken = await ReadRowAsync();

        await AddTransferEndAsync(PrinterEventType.TransferAborted, DownloadCommandId + 1);
        await transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        taken.TransferStartedAt.Should().BeNull("the queue's attempt is not the one running any more");
        taken.TransferCommandId.Should().Be(DownloadCommandId + 1);

        PrintFileOnPrinter ended = await ReadRowAsync();

        ended.TransferRefusalCount.Should().BeNull("a person's send counts against nothing");
        ended.HoldReason.Should().BeNull();
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

    /// <summary>
    /// A person's send is recorded as an attempt nothing in the queue waits on: its end settles it,
    /// and an abort only says the partial has gone - nothing is counted against the file.
    /// </summary>
    [Fact]
    public async Task ADirectSendsAbortIsSettledWithoutBeingCounted()
    {
        // Arrange
        PrintFile file = await SeedAsync();
        ConnectAnsweringDownloadsWith(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                            new CommandOutcome(PrinterEventType.TransferInfo, null))
        {
            CommandId = DownloadCommandId,
        }));
        TransferService transfers = _services.GetRequiredService<TransferService>();

        // Act - the send, then the printer giving it up
        await await SendDirectAsync(transfers, file);
        PrintFileOnPrinter sent = await ReadRowAsync();

        await AddTransferEndAsync(PrinterEventType.TransferAborted);
        await transfers.SettleAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        sent.TransferCommandId.Should().Be(DownloadCommandId, "the printer took it under that command");
        sent.TransferStartedAt.Should().BeNull("nothing in the queue waits on a person's send");

        PrintFileOnPrinter ended = await ReadRowAsync();

        ended.TransferCommandId.Should().BeNull("its end has been read");
        ended.Digest.Should().BeNull("the partial went with the abort");
        ended.TransferRefusalCount.Should().BeNull("a person's send counts against nothing");
    }

    private static Caller Owner => Caller.Scoped(1, CapabilitySet.Everything);

    /// <summary>The printer's answer to a download it took, under <paramref name="commandId"/>.</summary>
    private static CommandSendResult TakenAs(uint commandId)
    {
        return new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.TransferInfo, null))
        {
            CommandId = commandId,
        };
    }

    /// <summary>
    /// The printer's refusal of a download because its one transfer slot is taken, under
    /// <paramref name="commandId"/>.
    /// </summary>
    private static CommandSendResult RefusedAsBusy(uint commandId)
    {
        CommandOutcome refusal = new(PrinterEventType.Rejected, "Another transfer in progress")
        {
            MachineReason = "TRANSFER_IN_PROGRESS",
        };

        return new CommandSendResult(CommandSendOutcome.Completed, refusal) { CommandId = commandId };
    }

    /// <summary>
    /// The seeded file queued on the printer, with the queue's own transfer of it in flight under
    /// <see cref="DownloadCommandId"/> - stamped, as only the queue's attempts are.
    /// </summary>
    private async Task AddQueuedAttemptAsync(PrintFile file, DateTimeOffset started)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            PrintUuid = Guid.NewGuid(),
            Position = 0,
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format([Capability.Print]),
            QueuedAt = started,
        });
        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            DriveName = FileName,
            TransferStartedAt = started,
            TransferCommandId = DownloadCommandId,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The seeded file sent as a person sends it, once the printer and the bytes are read back.</summary>
    private async Task<Task<DirectSendResult>> SendDirectAsync(TransferService transfers, PrintFile file)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        Printer printer = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                     .Printers
                                     .AsNoTracking()
                                     .SingleAsync(TestContext.Current.CancellationToken);
        StoredFile stored = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>().FindForPrinting(file.UserId, file.Name)!;

        return transfers.SendDirectAsync(printer, file, stored, Owner, TestContext.Current.CancellationToken);
    }

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
    private async Task AddTransferEndAsync(PrinterEventType ending, uint startCommandId = DownloadCommandId)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        telemetry.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = PrinterId,
            Timestamp = _clock.GetUtcNow(),
            EventType = PrinterEventType.TransferInfo,
            CommandId = startCommandId,
            Payload = $"{{\"path\":\"/usb/{FileName}\",\"start_cmd_id\":{startCommandId},\"type\":\"FROM_CONNECT\"}}",
        });
        telemetry.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = PrinterId,
            Timestamp = _clock.GetUtcNow(),
            EventType = ending,
            Payload = $"{{\"start_cmd_id\":{startCommandId}}}",
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

    /// <summary>Runs what is posted to it where it is posted from, so a continuation runs inside the completion that wakes it.</summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }
}
