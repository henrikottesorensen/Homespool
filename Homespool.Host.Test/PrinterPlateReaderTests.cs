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
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterPlateReader"/>'s fetch, which nobody but a page render waits on.
/// </summary>
public sealed class PrinterPlateReaderTests : IDisposable
{
    private const int PrinterId = 1;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"plate-reader-{Guid.NewGuid():N}.sqlite");

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddYears(56));
    private readonly PrinterConnectionRegistry _registry = new(TimeProvider.System, NullLogger<PrinterConnectionRegistry>.Instance);

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
    /// A question the printer takes and never finishes gives up at the response timeout, and the
    /// fetch is asked again on a later render rather than left cached for the job for good.
    /// </summary>
    /// <remarks>
    /// The case: a Python-SDK printer accepts <c>SEND_JOB_INFO</c> and its telemetry goes on naming it,
    /// so nothing ends the command - a stuck PrusaLink handler. The fetch's own bound ends as a
    /// cancellation rather than a fault, which is why the retry has to treat the two alike.
    /// </remarks>
    [Fact]
    public async Task AQuestionThePrinterNeverFinishesGivesUpAndIsAskedAgain()
    {
        // Arrange
        await SeedAsync();

        int asked = 0;
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(default!, TestContext.Current.CancellationToken)
             .ReturnsForAnyArgs(call =>
             {
                 Interlocked.Increment(ref asked);

                 return Never(call.ArgAt<CancellationToken>(1));
             });
        _registry.Register(PrinterId, actor, overPlaintext: false);

        await using ServiceProvider services = NewServices();
        PrinterPlateReader reader = new(services.GetRequiredService<IServiceScopeFactory>(), _clock,
                                        NullLogger<PrinterPlateReader>.Instance, Options(TimeSpan.FromSeconds(30)));
        Caller caller = TestCallers.Scoped(1, Capability.ViewPrinter);

        PlateReading first = await reader.ReadAsync(PrinterId, jobId: 7, caller, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref asked) == 1);

        // Act
        _clock.Advance(PrinterPlateReader.RetryAfter + TimeSpan.FromSeconds(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _ = await reader.ReadAsync(PrinterId, jobId: 7, caller, TimeSpan.Zero, TestContext.Current.CancellationToken);

        // Assert
        first.Settled.Should().BeFalse();
        await WaitUntilAsync(() => Volatile.Read(ref asked) == 2);
    }

    /// <summary>A send that never ends unless its caller's token is cancelled.</summary>
    private static Task<CommandSendResult> Never(CancellationToken cancellationToken)
    {
        TaskCompletionSource<CommandSendResult> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));

        return never.Task;
    }

    private static IOptionsMonitor<PrusaConnectOptions> Options(TimeSpan responseTimeout)
    {
        IOptionsMonitor<PrusaConnectOptions> options = Substitute.For<IOptionsMonitor<PrusaConnectOptions>>();
        options.CurrentValue.Returns(new PrusaConnectOptions { CommandResponseTimeoutSeconds = responseTimeout.TotalSeconds });

        return options;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue("the fetch should have reached the printer by now");
    }

    private ServiceProvider NewServices()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<PrinterAccessService>();
        services.AddSingleton(_registry);
        services.AddScoped<PrinterCommandService>();

        return services.BuildServiceProvider();
    }

    private async Task SeedAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        await using HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TestAccounts.Add(context, 1);
        context.TeamMembers.Add(TestMemberships.Viewer(team.Id, 1));
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

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
