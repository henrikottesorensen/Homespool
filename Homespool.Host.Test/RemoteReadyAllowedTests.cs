using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="Printer.RemoteReadyAllowed"/> - who may set it, and what it does and does not gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flag is an enforced boundary as of 2026-08-30</b>, having been a policy on a button until
/// then. It used to be checked by the two page handlers on the way past, while
/// <c>PUT /api/v1/printers/{uuid}/command/ready</c> reached the same wire command without consulting
/// it - deliberately, on the argument that writing a script is already the deliberate act the walk to
/// the printer stood in for, and pinned here so that closing the gap would be a decision somebody
/// made rather than a tidy-up.
/// </para>
/// <para>
/// <b>That decision was made and went the other way.</b> The argument holds for the owner scripting
/// their own machine and holds less well for a member handed <see cref="Capability.Print"/> and a
/// personal access token, which is what the deployment actually permits. The check now lives on
/// <see cref="Printing.SetPrinterReady"/> as
/// <see cref="Printing.IPrinterIntent.RequiresRemoteReadyAllowed"/> and is applied by
/// <c>PrinterCommandService</c>, so it holds on whatever route reaches it; the API endpoint is
/// switched off besides, and can be switched back on without reopening the gap.
/// </para>
/// <para>
/// The permission split is the other half: <c>ControlPrinter</c> presses Set ready,
/// <c>ManagePrinter</c> decides whether pressing it is honest for this machine. They are different
/// questions about different timescales, so a member who may run a printer may not re-answer the
/// standing judgement about it.
/// </para>
/// </remarks>
public sealed class RemoteReadyAllowedTests : IDisposable
{
    private const long Reader = 1;
    private const long User = 2;
    private const long Manager = 3;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-remote-ready-{Guid.NewGuid():N}.db");

    private Guid _printerUuid;

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
    /// <b>Off unless somebody turns it on</b> - the default is where the safety of the whole feature
    /// lives, so it is asserted rather than assumed from the column type.
    /// </summary>
    [Fact]
    public async Task ANewPrinterMayNotBeReadiedFromItsPage()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        // Act
        Printer printer = await context.Printers.SingleAsync(p => p.Uuid == _printerUuid,
                                                             TestContext.Current.CancellationToken);

        // Assert
        printer.RemoteReadyAllowed.Should().BeFalse();
    }

    /// <summary>Turning it on and off again, by somebody holding <c>CanManage</c>.</summary>
    [Fact]
    public async Task AManagerCanAllowItAndTakeItBack()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterQueryService service = ServiceFor(context);

        // Act
        bool? allowed = await service.SetRemoteReadyAllowedAsync(_printerUuid, Caller.Unscoped(Manager), true,
                                                                 TestContext.Current.CancellationToken);

        // Assert
        allowed.Should().BeTrue();
        (await ReadFlagAsync(context)).Should().BeTrue();

        // Act
        await service.SetRemoteReadyAllowedAsync(_printerUuid, Caller.Unscoped(Manager), false,
                                                 TestContext.Current.CancellationToken);

        // Assert
        (await ReadFlagAsync(context)).Should().BeFalse();
    }

    /// <summary>
    /// <b>Running a printer is not deciding for it.</b> <c>CanUse</c> presses Set ready; only
    /// <c>CanManage</c> decides whether that press can be honest, which is the split this feature
    /// rests on.
    /// </summary>
    [Fact]
    public async Task SomebodyWhoMayControlThePrinterStillMayNotAllowIt()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterQueryService service = ServiceFor(context);

        // Act & Assert
        await FluentActions
              .Awaiting(() => service.SetRemoteReadyAllowedAsync(_printerUuid, Caller.Unscoped(User), true,
                                                                 TestContext.Current.CancellationToken))
              .Should().ThrowAsync<TeamAccessDeniedException>();

        (await ReadFlagAsync(context)).Should().BeFalse();
    }

    /// <summary>
    /// A caller who cannot read the printer gets <c>null</c> rather than a refusal, so the two cannot
    /// be told apart - the same rule the rest of this service follows, for the same reason.
    /// </summary>
    [Fact]
    public async Task APrinterYouCannotSeeAnswersNullRatherThanForbidden()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterQueryService service = ServiceFor(context);

        // Act
        bool? unknown = await service.SetRemoteReadyAllowedAsync(Guid.NewGuid(), Caller.Unscoped(Manager), true,
                                                                 TestContext.Current.CancellationToken);

        // Assert
        unknown.Should().BeNull();
    }

    /// <summary>
    /// <b>The flag is not a capability.</b> Turning it off takes nothing away from anybody: the same
    /// members may use the printer, and what changes is what the machine will accept remotely, not
    /// who may ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to pin the opposite of what it now pins</b>, and the difference is worth
    /// stating. It read: the gate is deliberately not on the command path, the page handler checks
    /// the flag and <c>PUT /api/v1/printers/{uuid}/command/ready</c> does not - and it closed by
    /// saying that if it ever had to change, the decision had been reversed. It has been (2026-08-30):
    /// <see cref="Printing.SetPrinterReady"/> now declares
    /// <see cref="Printing.IPrinterIntent.RequiresRemoteReadyAllowed"/> and
    /// <c>PrinterCommandService</c> enforces it for every route, with the API endpoint switched off
    /// besides.
    /// </para>
    /// <para>
    /// What survives that reversal is this assertion, which was always the honest half: the flag
    /// governs the machine, not the membership. <c>ReadyingIsRefusedWhenThePrinterDoesNotAllowIt
    /// Remotely</c> in <c>PrinterCommandServiceTests</c> pins the other half - that it does now stop
    /// the command - so neither half can be quietly dropped.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TurningItOffChangesNoPermissionOnThePrinterItself()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterQueryService service = ServiceFor(context);
        PrinterAccessService access = new(context, NullLogger<PrinterAccessService>.Instance);

        await service.SetRemoteReadyAllowedAsync(_printerUuid, Caller.Unscoped(Manager), false,
                                                 TestContext.Current.CancellationToken);

        // Act
        bool mayControl = await access.AllowsAsync(1, Caller.Unscoped(User), Capability.ControlPrinter,
                                                   TestContext.Current.CancellationToken);

        // Assert
        mayControl.Should().BeTrue();
    }

    private static PrinterQueryService ServiceFor(HomespoolDbContext context)
    {
        return new PrinterQueryService(context, new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance), new TeamCapabilityLookup(context), TimeProvider.System);
    }

    private async Task<bool> ReadFlagAsync(HomespoolDbContext context)
    {
        Printer printer = await context.Printers
                                       .AsNoTracking()
                                       .SingleAsync(p => p.Uuid == _printerUuid, TestContext.Current.CancellationToken);

        return printer.RemoteReadyAllowed;
    }

    private async Task<HomespoolDbContext> SeedAsync()
    {
        HomespoolDbContext context = new(new DbContextOptionsBuilder<HomespoolDbContext>()
                                         .UseSqlite($"Data Source={_databasePath}")
                                         .Options);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = Reader, Capabilities = TestMemberships.Graded(true, false, false) });
        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = User, Capabilities = TestMemberships.Graded(true, true, false) });
        context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = Manager,
            Capabilities = TestMemberships.Graded(true, true, true),
        });

        _printerUuid = Guid.NewGuid();
        context.Printers.Add(new Printer { Id = 1, Uuid = _printerUuid, TeamId = team.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
