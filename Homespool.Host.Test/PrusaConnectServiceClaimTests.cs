using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaConnectService.ClaimPrinterAsync"/> - the app-facing half of enrolment, where a
/// signed-in user redeems the code a printer is displaying (AGENT-NOTES phase-1.5 §15 step 7a).
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching <c>PrinterRegistrationTests</c>,
/// since the code lookup depends on provider behaviour for the timestamp comparison.
/// </remarks>
public sealed class PrusaConnectServiceClaimTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-claim-{Guid.NewGuid():N}.db");

    private static PrusaConnectService NewService(HomespoolDbContext context, int lifetimeMinutes = 60)
    {
        return new(context,
                   new CodeGenerator(),
                   new TokenService(),
                   new TeamService(context),
                   TimeProvider.System, NullLogger<PrusaConnectService>.Instance,
                   Options.Create(new PrusaConnectOptions { RegistrationCodeLifetimeMinutes = lifetimeMinutes }));
    }

    private static RegisterPrinterRequestDTO PrinterRequest(string serial = "15715-4842441651816441",
                                                            string fingerprint =
                                                                "SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L")
    {
        return new()
        {
            SerialNumber = serial,
            FingerPrint = fingerprint,
            PrinterType = "1.3.5",
            Firmware = "6.4.0+11974",
        };
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

    private static async Task<TeamMember> AddTeamAsync(HomespoolDbContext context, long userId, bool canManage, bool isDefault)
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
                    Capabilities = TestMemberships.Graded(true, true, canManage),
                    IsDefault = isDefault,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team.Members.Single();
    }

    /// <summary>
    /// Claiming with no team id lands the printer in the caller's default team, sets a fresh Uuid,
    /// and links the registration - the three things §14/§15 required and nothing generated before.
    /// </summary>
    [Fact]
    public async Task ClaimingWithNoTeamIdUsesTheCallersDefaultTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        TeamMember defaultTeam = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Printer printer = await service.ClaimPrinterAsync(code, "My printer", "Office", teamId: null, caller: Caller.Unscoped(1));

        // Assert
        printer.TeamId.Should().Be(defaultTeam.TeamId);
        printer.Uuid.Should().NotBe(Guid.Empty);
        printer.Type.Should().Be(PrinterType.PrusaConnect);
        printer.Status.Should().Be(PrinterStatus.Unknown);
        printer.Name.Should().Be("My printer");
        printer.Location.Should().Be("Office");

        PrusaConnectRegistration stored =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        stored.PrinterId.Should().Be(printer.Id);
    }

    /// <summary>
    /// An explicit team id is honoured when the caller has CanManage on it - not just any membership.
    /// </summary>
    [Fact]
    public async Task ClaimingWithAnExplicitTeamIdUsesThatTeamWhenTheCallerCanManageIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember managedTeam = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: false);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Printer printer = await service.ClaimPrinterAsync(code, null, null, teamId: managedTeam.TeamId, caller: Caller.Unscoped(1));

        // Assert
        printer.TeamId.Should().Be(managedTeam.TeamId);
    }

    /// <summary>
    /// A team the caller can only use, not manage, is rejected - claiming is a structural change,
    /// the same tier as inviting a member.
    /// </summary>
    [Fact]
    public async Task ClaimingIntoATeamTheCallerCannotManageIsRejected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember unmanaged = await AddTeamAsync(context, userId: 1, canManage: false, isDefault: false);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: unmanaged.TeamId, caller: Caller.Unscoped(1));

        // Assert
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A team the caller isn't a member of at all is rejected the same way as one they can't manage -
    /// membership isn't leaked by the failure mode.
    /// </summary>
    [Fact]
    public async Task ClaimingIntoATeamTheCallerIsNotAMemberOfIsRejected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: someoneElses.TeamId, caller: Caller.Unscoped(1));

        // Assert
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A second claim of the same code is rejected once the first has succeeded - the concrete answer
    /// to the "concurrent claim" question phase-1.5 §15 step 7 left open.
    /// </summary>
    [Fact]
    public async Task ASecondClaimOfAnAlreadyClaimedCodeIsRejected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        await service.ClaimPrinterAsync(code, null, null, teamId: null, caller: Caller.Unscoped(1));

        // Act
        Func<Task> secondClaim = () => service.ClaimPrinterAsync(code, null, null, teamId: null, caller: Caller.Unscoped(2));

        // Assert
        await secondClaim.Should().ThrowAsync<RegistrationAlreadyClaimedException>();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should()
                                                                                  .Be(
                                                                                      1,
                                                                                      "the second claim must not create a competing printer");
    }

    /// <summary>
    /// The code is not consumed by claiming - the printer still has to poll and redeem it separately,
    /// so claiming first must not strand a printer that hasn't polled since.
    /// </summary>
    [Fact]
    public async Task ClaimingDoesNotConsumeTheCode()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        await service.ClaimPrinterAsync(code, null, null, teamId: null, caller: Caller.Unscoped(1));

        // Assert
        (await service.GetToken(code)).Should().NotBeNullOrWhiteSpace("the printer still has to redeem the code itself");
    }

    /// <summary>
    /// An unknown or expired code is rejected the same way <see cref="PrusaConnectService.GetToken"/>
    /// rejects one, sharing the same lookup.
    /// </summary>
    [Fact]
    public async Task ClaimingWithAnUnknownOrExpiredCodeIsRejected()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        Func<Task> unknown = () => service.ClaimPrinterAsync("NEVER-ISSUED", null, null, teamId: null, caller: Caller.Unscoped(1));

        // Assert
        await unknown.Should().ThrowAsync<PrinterNotFoundException>();
    }

    /// <summary>
    /// A caller with no default team - should be unreachable given every account gets one at
    /// creation - fails closed rather than creating a teamless printer.
    /// </summary>
    [Fact]
    public async Task ClaimingWithNoTeamIdAndNoDefaultTeamFailsClosed()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: null, caller: Caller.Unscoped(1));

        // Assert
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();
    }
}
