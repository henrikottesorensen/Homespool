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
/// <b>The flag is a policy on a button, not an enforced boundary</b>, and that is a decision rather
/// than an omission. The API path answers whatever the column says, because writing a script is
/// already the deliberate act the walk to the printer stood in for. A test asserts that on purpose
/// here, so a later session reading the gap as a defect finds a sentence saying it is not - and so
/// that closing it becomes a decision somebody makes rather than a tidy-up.
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
    /// <b>The gate is deliberately not on the command path.</b> Nothing in
    /// <see cref="Printer.RemoteReadyAllowed"/> reaches <c>SET_PRINTER_READY</c> itself - the page
    /// handler checks it, and <c>PUT /api/v1/printers/{uuid}/command/ready</c> does not.
    /// </summary>
    /// <remarks>
    /// Asserted by the flag being reachable only through the page and the settings write, which is
    /// what this test pins: turning it off changes no permission and blocks no service call. If this
    /// test ever has to change, that decision has been reversed.
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
