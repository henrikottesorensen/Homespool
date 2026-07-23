using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrusaConnectService.ClaimPrinterAsync"/> - the app-facing half of enrollment, where a
/// signed-in user redeems the code a printer is displaying (AGENT-NOTES phase-1.5 §15 step 7a).
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching <c>PrinterRegistrationTests</c>,
/// since the code lookup depends on provider behaviour for the timestamp comparison.
/// </remarks>
public sealed class PrusaConnectServiceClaimTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-claim-{Guid.NewGuid():N}.db");

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
    }

    private async Task<PSDbContext> MigratedContextAsync()
    {
        PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        return context;
    }

    private static PrusaConnectService NewService(PSDbContext context, int lifetimeMinutes = 60) =>
        new(context,
            new CodeGenerator(),
            new TokenService(),
            new TeamService(context),
            NullLogger<PrusaConnectService>.Instance,
            Options.Create(new PrusaConnectOptions { RegistrationCodeLifetimeMinutes = lifetimeMinutes }));

    private static RegisterPrinterRequestDTO PrinterRequest(string serial = "15715-4842441651816441",
                                                             string fingerprint = "SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L") => new()
    {
        SerialNumber = serial,
        FingerPrint = fingerprint,
        PrinterType = "1.3.5",
        Firmware = "6.4.0+11974",
    };

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

    private static async Task<TeamMember> AddTeamAsync(PSDbContext context, long userId, bool canManage, bool isDefault)
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
                    CanRead = true,
                    CanUse = true,
                    CanManage = canManage,
                    IsDefault = isDefault,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync();

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        TeamMember defaultTeam = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Printer printer = await service.ClaimPrinterAsync(code, "My printer", "Office", teamId: null, userId: 1);

        // Assert
        printer.TeamId.Should().Be(defaultTeam.TeamId);
        printer.Uuid.Should().NotBe(Guid.Empty);
        printer.Type.Should().Be(PrinterType.PrusaConnect);
        printer.Status.Should().Be(PrinterStatus.Unknown);
        printer.Name.Should().Be("My printer");
        printer.Location.Should().Be("Office");

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.PrinterId.Should().Be(printer.Id);
    }

    /// <summary>
    /// An explicit team id is honoured when the caller has CanManage on it - not just any membership.
    /// </summary>
    [Fact]
    public async Task ClaimingWithAnExplicitTeamIdUsesThatTeamWhenTheCallerCanManageIt()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember managedTeam = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: false);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Printer printer = await service.ClaimPrinterAsync(code, null, null, teamId: managedTeam.TeamId, userId: 1);

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember unmanaged = await AddTeamAsync(context, userId: 1, canManage: false, isDefault: false);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: unmanaged.TeamId, userId: 1);

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: someoneElses.TeamId, userId: 1);

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        await service.ClaimPrinterAsync(code, null, null, teamId: null, userId: 1);

        // Act
        Func<Task> secondClaim = () => service.ClaimPrinterAsync(code, null, null, teamId: null, userId: 2);

        // Assert
        await secondClaim.Should().ThrowAsync<RegistrationAlreadyClaimedException>();
        (await context.Printers.CountAsync()).Should().Be(1, "the second claim must not create a competing printer");
    }

    /// <summary>
    /// The code is not consumed by claiming - the printer still has to poll and redeem it separately,
    /// so claiming first must not strand a printer that hasn't polled since.
    /// </summary>
    [Fact]
    public async Task ClaimingDoesNotConsumeTheCode()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        await service.ClaimPrinterAsync(code, null, null, teamId: null, userId: 1);

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        Func<Task> unknown = () => service.ClaimPrinterAsync("NEVER-ISSUED", null, null, teamId: null, userId: 1);

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(PrinterRequest())).TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, null, null, teamId: null, userId: 1);

        // Assert
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();
    }
}
