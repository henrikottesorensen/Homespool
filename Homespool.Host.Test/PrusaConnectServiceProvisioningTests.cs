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
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The USB-key enrolment channel's server half: <see cref="PrusaConnectService.ProvisionPrinterAsync"/>
/// mints a printer and a pre-provisioned token for a <c>prusa_printer_settings.ini</c> snippet, and
/// <see cref="PrusaConnectService.RegenerateProvisioningTokenAsync"/> reissues one that was never used.
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching every other phase-1.5 service
/// test - several assertions here depend on the unique indexes actually being enforced.
/// </remarks>
public sealed class PrusaConnectServiceProvisioningTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-prov-{Guid.NewGuid():N}.db");

    private static PrusaConnectService NewService(HSDbContext context)
    {
        return new(context,
            new CodeGenerator(),
            new TokenService(),
            new TeamService(context),
            TimeProvider.System, NullLogger<PrusaConnectService>.Instance,
            Options.Create(new PrusaConnectOptions()));
    }

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
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

    private static async Task<TeamMember> AddTeamAsync(HSDbContext context, long userId, bool canManage, bool isDefault)
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team.Members.Single();
    }

    // ---------- provisioning ----------

    /// <summary>
    /// Provisioning creates the printer up front - unlike the code exchange, where the printer row
    /// does not exist until a user claims a code - and lands it in the caller's default team.
    /// </summary>
    [Fact]
    public async Task ProvisioningCreatesThePrinterUpFrontInTheCallersDefaultTeam()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        TeamMember defaultTeam = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        (Printer printer, string token) = await service.ProvisionPrinterAsync("Bench printer", "Workshop", teamId: null, userId: 1);

        // Assert
        printer.TeamId.Should().Be(defaultTeam.TeamId);
        printer.Uuid.Should().NotBe(Guid.Empty);
        printer.Type.Should().Be(PrinterType.PrusaConnect);
        printer.Status.Should().Be(PrinterStatus.Unknown);
        printer.Name.Should().Be("Bench printer");
        printer.Location.Should().Be("Workshop");

        token.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Only the hash is persisted, and it verifies against the token the caller was handed once.
    /// </summary>
    /// <remarks>
    /// This token goes onto a USB stick in plaintext and authenticates every request the printer ever
    /// makes, so a database copy would be worth stealing - the same reasoning as the code-exchange
    /// token, and the reason provisioning returns the plaintext rather than storing it.
    /// </remarks>
    [Fact]
    public async Task ProvisioningStoresOnlyTheTokensHash()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        (Printer printer, string token) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // Assert
        PrusaConnectProvisioning stored = await context.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken);

        stored.PrinterId.Should().Be(printer.Id);
        stored.HashedToken.Should().NotBe(token, "the token must never be stored in the clear");
        new TokenService().VerifyToken(token, stored.HashedToken).Should().BeTrue();
    }

    /// <summary>
    /// The token fits the firmware's <c>connect_token</c> buffer.
    /// </summary>
    /// <remarks>
    /// <c>config_store_ns::connect_token_size</c> is 20, and <c>connect_ini_handler</c> silently
    /// <em>rejects</em> a longer value (<c>len &lt;= connect_token_size</c>), so an over-long token
    /// would leave the printer with no token at all rather than a truncated one. Nothing else in the
    /// codebase references that limit, so this is the guard rail.
    /// </remarks>
    [Fact]
    public async Task TheProvisionedTokenFitsTheFirmwareTokenBuffer()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        (Printer _, string token) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // Assert
        token.Length.Should().BeLessThanOrEqualTo(TokenService.PrinterTokenLength);
    }

    /// <summary>
    /// An explicit team is honoured when the caller can manage it - the same bar as claiming, since
    /// adding a printer is a structural change to the team either way.
    /// </summary>
    [Fact]
    public async Task ProvisioningIntoAnExplicitTeamRequiresCanManage()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        TeamMember managed = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: false);

        // Act
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: managed.TeamId, userId: 1);

        // Assert
        printer.TeamId.Should().Be(managed.TeamId);
    }

    /// <summary>
    /// A team the caller can only use, and one they are not in at all, are refused identically -
    /// membership is not leaked by the failure mode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisioningIntoATeamTheCallerCannotManageIsRejected(bool someoneElsesTeam)
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        TeamMember target = someoneElsesTeam
            ? await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true)
            : await AddTeamAsync(context, userId: 1, canManage: false, isDefault: false);

        // Act
        Func<Task> provision = () => service.ProvisionPrinterAsync(null, null, teamId: target.TeamId, userId: 1);

        // Assert
        await provision.Should().ThrowAsync<TeamAccessDeniedException>();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse("a refused provision must not leave a printer behind");
    }

    /// <summary>
    /// A caller with no default team fails closed rather than creating a teamless printer - the same
    /// guard the claim path has, and unreachable in practice since every account gets a team.
    /// </summary>
    [Fact]
    public async Task ProvisioningWithNoDefaultTeamFailsClosed()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        // Act
        Func<Task> provision = () => service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // Assert
        await provision.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// Provisioning does not touch the code-exchange tables at all: no registration is created, and
    /// nothing is enrolled until the printer actually presents the token.
    /// </summary>
    [Fact]
    public async Task ProvisioningEnrolsNothingUntilThePrinterMakesContact()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // Assert
        (await context.PrusaConnectRegistrations.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse("provisioning bypasses /p/register entirely");
        (await context.PrusaConnectAuthentication.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse("enrolment completes at first contact, not here");
    }

    // ---------- regenerate ----------

    /// <summary>
    /// Reissuing replaces the token in place: the old one stops verifying, the new one starts, and the
    /// printer is not duplicated.
    /// </summary>
    [Fact]
    public async Task RegeneratingReplacesTheTokenInPlace()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string original) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // Act
        string reissued = await service.RegenerateProvisioningTokenAsync(printer.Id, userId: 1);

        // Assert
        reissued.Should().NotBe(original);

        PrusaConnectProvisioning stored = await context.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken);
        TokenService tokenService = new();

        tokenService.VerifyToken(reissued, stored.HashedToken).Should().BeTrue("the reissued token must work");
        tokenService.VerifyToken(original, stored.HashedToken).Should()
            .BeFalse("the token on the discarded USB stick must stop working");

        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "reissuing must not create a second printer");
    }

    /// <summary>
    /// Reissuing needs the same permission as provisioning did: a teammate who can read and use the
    /// printer, but not manage its team, cannot mint a fresh credential for it.
    /// </summary>
    /// <remarks>
    /// The member-without-CanManage case specifically, not a stranger - a stranger is refused by the
    /// membership lookup alone, which would leave the permission check itself unguarded.
    /// </remarks>
    [Fact]
    public async Task RegeneratingRequiresCanManageOnThePrintersTeam()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // a genuine member of the printer's own team, but without CanManage
        context.TeamMembers.Add(new TeamMember
        {
            TeamId = printer.TeamId,
            UserId = 2,
            CanRead = true,
            CanUse = true,
            CanManage = false,
            IsDefault = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> regenerate = () => service.RegenerateProvisioningTokenAsync(printer.Id, userId: 2);

        // Assert
        await regenerate.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// Someone with no membership of the printer's team at all is refused identically - the failure
    /// mode does not distinguish "not permitted" from "not a member".
    /// </summary>
    [Fact]
    public async Task RegeneratingAsAStrangerToThePrintersTeamIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);

        // Act
        Func<Task> regenerate = () => service.RegenerateProvisioningTokenAsync(printer.Id, userId: 2);

        // Assert
        await regenerate.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A printer id that does not exist is a not-found, distinct from "exists but nothing to reissue".
    /// </summary>
    [Fact]
    public async Task RegeneratingAnUnknownPrinterIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        // Act
        Func<Task> regenerate = () => NewService(context).RegenerateProvisioningTokenAsync(printerId: 999, userId: 1);

        // Assert
        await regenerate.Should().ThrowAsync<PrinterNotFoundException>();
    }

    /// <summary>
    /// A printer that was never USB-provisioned has no outstanding token to reissue.
    /// </summary>
    [Fact]
    public async Task RegeneratingAPrinterThatWasNeverProvisionedIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.TeamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> regenerate = () => service.RegenerateProvisioningTokenAsync(printer.Id, userId: 1);

        // Assert
        await regenerate.Should().ThrowAsync<ProvisioningTokenNotFoundException>();
    }

    /// <summary>
    /// An already-enrolled printer gets a fresh outstanding token, rather than being refused: this is
    /// the supported way to write a new USB stick for a printer that is already connected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding deletes the provisioning row, so an enrolled printer has none - the state is identical
    /// to "never provisioned" in this table, and the two are told apart by the enrolled table. The
    /// alternative to allowing it, provisioning the printer afresh, mints a second printer whose token
    /// the auth handler will not bind to the existing enrolment (see
    /// <c>PrusaConnectFingerprintIdentityTests</c>).
    /// </para>
    /// <para>
    /// The enrolled credential is deliberately untouched here: the printer keeps authenticating with
    /// the token it holds until the reissued one is actually presented, so writing a stick and never
    /// using it costs nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RegeneratingAnAlreadyEnrolledPrinterIssuesAFreshOutstandingToken()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        // first contact: the token is promoted into the enrolled table and the provisioning row goes
        TokenService tokenService = new();
        string enrolledToken = tokenService.GenerateToken();

        context.PrusaConnectProvisionings.RemoveRange(context.PrusaConnectProvisionings);
        context.PrusaConnectAuthentication.Add(new PrusaConnectAuthenticationData
        {
            PrinterId = printer.Id,
            FingerPrintKey = "SUDBAJQ78CTJBNA8",
            HashedToken = tokenService.HashToken(enrolledToken),
            EnrolledAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        string reissued = await service.RegenerateProvisioningTokenAsync(printer.Id, userId: 1);

        // Assert
        PrusaConnectProvisioning outstanding = await context.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken);

        outstanding.PrinterId.Should().Be(printer.Id);
        tokenService.VerifyToken(reissued, outstanding.HashedToken).Should().BeTrue();

        PrusaConnectAuthenticationData credential = await context.PrusaConnectAuthentication.SingleAsync(TestContext.Current.CancellationToken);

        tokenService.VerifyToken(enrolledToken, credential.HashedToken).Should()
            .BeTrue("the printer must keep working until it is actually given the reissued token");

        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "reissuing must not create a second printer");
    }

    /// <summary>
    /// A printer that is neither provisioned nor enrolled has nothing for a reissued token to attach
    /// to.
    /// </summary>
    [Fact]
    public async Task RegeneratingAPrinterThatIsNeitherProvisionedNorEnrolledIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        context.PrusaConnectProvisionings.RemoveRange(context.PrusaConnectProvisionings);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> regenerate = () => service.RegenerateProvisioningTokenAsync(printer.Id, userId: 1);

        // Assert
        await regenerate.Should().ThrowAsync<ProvisioningTokenNotFoundException>();
    }

    /// <summary>
    /// A printer carries at most one outstanding provisioning token, enforced by the database.
    /// </summary>
    /// <remarks>
    /// The unique index on <c>PrinterId</c> is what makes "a row exists" mean "exactly one unbound
    /// token", which is the invariant the regenerate guard and the auth handler's scan both rest on.
    /// </remarks>
    [Fact]
    public async Task APrinterCannotHoldTwoOutstandingProvisioningTokens()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);
        (Printer printer, string _) = await service.ProvisionPrinterAsync(null, null, teamId: null, userId: 1);

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // Act
        Func<Task> second = () => context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert
        await second.Should().ThrowAsync<DbUpdateException>();
    }
}
