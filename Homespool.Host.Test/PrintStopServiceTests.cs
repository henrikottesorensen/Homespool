using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintStopService"/> - who stopped a print, which the printer cannot report.
/// </summary>
/// <remarks>
/// <para>
/// The column had no writer at all until 2026-08-03 while two surfaces could already send
/// <c>STOP_PRINT</c>, so every stop made here was recorded as one made at the panel. These cover the
/// writing rule rather than the sending, which <see cref="PrinterCommandService"/> already owns.
/// </para>
/// <para>
/// The awkward half is timing: <c>STOP_PRINT</c>'s ack means the abort was accepted, not that the
/// print has ended, so the queue's loop may close the row while the call is still in flight. Three of
/// these are about that window.
/// </para>
/// </remarks>
public sealed class PrintStopServiceTests : IDisposable
{
    private const int PrinterId = 1;
    private const long Stopper = 1;
    private const long SomebodyElse = 2;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-stop-{Guid.NewGuid():N}.db");
    private readonly PrinterConnectionRegistry _registry = new(NullLogger<PrinterConnectionRegistry>.Instance);

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

    /// <summary>The ordinary case: a running print, a stop, and a name against it afterwards.</summary>
    [Fact]
    public async Task AStopIsAttributedToWhoeverAskedForIt()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Printing, ended: false);
        Connect(PrinterEventType.Finished);

        // Act
        await NewService(context).StopAsync(PrinterId, Stopper, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().Be(Stopper);
    }

    /// <summary>
    /// A refusal writes nothing - the print carried on, so naming a stopper would be the same lie
    /// pointing the other way.
    /// </summary>
    [Fact]
    public async Task ARefusedStopAttributesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Printing, ended: false);
        Connect(PrinterEventType.Rejected, "No print to stop");

        // Act
        await NewService(context).StopAsync(PrinterId, Stopper, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().BeNull("the printer refused, so nobody stopped anything");
    }

    /// <summary>
    /// The race this method is arranged around: the loop sees the printer stop and closes the row
    /// before the ack is handled. The attribution still lands, because the id was read first.
    /// </summary>
    [Fact]
    public async Task AStopIsStillAttributedWhenTheLoopClosedTheRowFirst()
    {
        // Arrange - closed as Stopped while the command was in flight
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Printing, ended: false);
        ConnectClosingTheRowMidFlight(PrintState.Stopped);

        // Act
        await NewService(context).StopAsync(PrinterId, Stopper, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().Be(Stopper, "the row was found before the command went out");
    }

    /// <summary>
    /// And the other side of that window: a print that finished on its own in the same moment is not
    /// claimed. Under-claiming is the safe direction - it costs a name, where over-claiming would put
    /// a stopper on a print nobody stopped.
    /// </summary>
    [Fact]
    public async Task APrintThatFinishedInTheRaceIsNotAttributed()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Printing, ended: false);
        ConnectClosingTheRowMidFlight(PrintState.Finished);

        // Act
        await NewService(context).StopAsync(PrinterId, Stopper, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().BeNull("it reached its finished screen, so nobody's stop caused it");
    }

    /// <summary>
    /// Two people pressing stop seconds apart both get an accepted ack. The first caused it.
    /// </summary>
    [Fact]
    public async Task TheFirstStopKeepsTheAttribution()
    {
        // Arrange - already attributed
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Printing, ended: false, stoppedBy: Stopper);
        Connect(PrinterEventType.Finished);

        // Act
        await NewService(context).StopAsync(PrinterId, SomebodyElse, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().Be(Stopper, "the second stop did not cause what the first one did");
    }

    /// <summary>
    /// A stop accepted with nothing open is not an error. The command still goes out - the printer is
    /// the authority on whether there is anything to stop.
    /// </summary>
    [Fact]
    public async Task AStopWithNoOpenPrintWritesNothing()
    {
        // Arrange - history, but nothing running
        await using HomespoolDbContext context = await SeedAsync();
        await AddPrintAsync(context, PrintState.Finished, ended: true);
        Connect(PrinterEventType.Finished);

        // Act
        CommandOutcome? outcome =
            await NewService(context).StopAsync(PrinterId, Stopper, TestContext.Current.CancellationToken);

        // Assert
        outcome!.EventType.Should().Be(PrinterEventType.Finished, "the send is not conditional on our own bookkeeping");

        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.StoppedByUserId.Should().BeNull();
    }

    private PrintStopService NewService(HomespoolDbContext context)
    {
        return new PrintStopService(context,
                                    new PrinterCommandService(new PrinterAccessService(context), _registry),
                                    NullLogger<PrintStopService>.Instance);
    }

    private void Connect(PrinterEventType reply, string? reason = null)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(reply, reason))));

        _registry.Register(PrinterId, actor);
    }

    /// <summary>
    /// An actor that closes the open print as part of answering, standing in for the queue's loop
    /// getting there first. Its own context, because that is what the loop has.
    /// </summary>
    private void ConnectClosingTheRowMidFlight(PrintState outcome)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(async _ =>
             {
                 await using HomespoolDbContext loopContext = NewContext();
                 PrintJob open = await loopContext.PrintJobs.SingleAsync(job => job.EndedAt == null,
                                                                         TestContext.Current.CancellationToken);

                 open.State = outcome;
                 open.EndedAt = DateTimeOffset.UnixEpoch.AddYears(56);
                 await loopContext.SaveChangesAsync(TestContext.Current.CancellationToken);

                 return new CommandSendResult(CommandSendOutcome.Completed,
                                              new CommandOutcome(PrinterEventType.Finished, null));
             });

        _registry.Register(PrinterId, actor);
    }

    private HomespoolDbContext NewContext()
    {
        return new HomespoolDbContext(new DbContextOptionsBuilder<HomespoolDbContext>()
                                      .UseSqlite($"Data Source={_databasePath}")
                                      .Options);
    }

    private async Task AddPrintAsync(HomespoolDbContext context, PrintState outcome, bool ended, long? stoppedBy = null)
    {
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "running.bgcode",
            QueuedByUserId = Stopper,
            StartedAt = DateTimeOffset.UnixEpoch.AddYears(56),
            EndedAt = ended ? DateTimeOffset.UnixEpoch.AddYears(56) : null,
            State = outcome,
            StoppedByUserId = stoppedBy,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HomespoolDbContext> SeedAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach ((long id, string email) in new[] { (Stopper, "owner@example.com"), (SomebodyElse, "other@example.com") })
        {
            context.Users.Add(new HSUser(email)
            {
                Id = id,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
            });
        }

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = Stopper, CanRead = true, CanUse = true });
        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = SomebodyElse, CanRead = true, CanUse = true });
        context.Printers.Add(new Printer { Id = PrinterId, Uuid = Guid.NewGuid(), TeamId = team.Id });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
