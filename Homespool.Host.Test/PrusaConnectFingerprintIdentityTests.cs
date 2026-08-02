using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Printer identity across the two enrolment channels, exercised the way a real printer drives it:
/// enroll through the service, then authenticate through the handler with the fingerprint the
/// firmware actually puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>The firmware sends its fingerprint in two lengths.</b> <c>/p/register</c>'s JSON body carries
/// the full 50 characters (<c>registrator.cpp:61</c>, a plain <c>JSON_FIELD_STR</c>); every HTTP
/// header - including the <c>/p/ws</c> upgrade - carries a 16-character truncation, because both
/// <c>BasicRequest</c> and <c>UpgradeRequest</c> build the header with an explicit
/// <c>FINGERPRINT_HDR_SIZE</c> size limit (<c>connect.cpp:137</c> and <c>:164</c>, firmware
/// <c>v6.6.0</c>, SHA <c>e96ce2b</c>). The short form is always an exact prefix of the long one.
/// </para>
/// <para>
/// <b>This is why these tests exist and why the existing suites miss the defect.</b>
/// <c>PrusaConnectPrinterAuthenticationHandlerTests</c> authenticates with the 50-character constant,
/// which no printer ever presents on a header; <c>PrusaConnectServiceClaimTests</c> stops at the
/// claim and never authenticates afterwards. Each half is self-consistent, so the mismatch only
/// appears when one printer is driven through both - as it is here.
/// </para>
/// <para>
/// Run against real SQLite, matching every other phase-1.5 suite: the unique fingerprint index is
/// part of what is under test.
/// </para>
/// </remarks>
public sealed class PrusaConnectFingerprintIdentityTests : IDisposable
{
    /// <summary>What <c>/p/register</c>'s body carries: the full <c>printerHash()</c> output.</summary>
    private const string BodyFingerprint = "SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L";

    /// <summary>What every header carries, <c>/p/ws</c>'s upgrade included: the first 16 characters.</summary>
    private const string HeaderFingerprint = "SUDBAJQ78CTJBNA8";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-fpid-{Guid.NewGuid():N}.db");

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

    private static RegisterPrinterRequestDTO PrinterRequest()
    {
        return new()
        {
            SerialNumber = "15715-4842441651816441",
            FingerPrint = BodyFingerprint,
            PrinterType = "1.3.5",
            Firmware = "6.4.0+11974",
        };
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

    /// <summary>
    /// One request through the handler, in its own context, exactly as the authentication middleware
    /// would run it. Copied in shape from <c>PrusaConnectPrinterAuthenticationHandlerTests</c>.
    /// </summary>
    private async Task<AuthenticateResult> AuthenticateAsync(string fingerprint, string token)
    {
        await using HSDbContext context = NewContext();

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers[Headers.Fingerprint] = fingerprint;
        httpContext.Request.Headers[Headers.Token] = token;
        httpContext.Request.Headers[Headers.UserAgentPrinter] = "MK3.5";
        httpContext.Request.Headers[Headers.UserAgentVersion] = "6.4.0+11974";

        PrusaConnectPrinterAuthenticationHandler handler = new(
            context,
            new TokenService(),
            new UnitOfWork(context),
            new StaticOptionsMonitor(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(Schemes.PrusaConnectPrinter,
                                     Schemes.PrusaConnectPrinter,
                                     typeof(PrusaConnectPrinterAuthenticationHandler)),
            httpContext);

        return await handler.AuthenticateAsync();
    }

    /// <summary>
    /// The code-exchange channel end to end: the printer registers, a user claims the code, the
    /// printer polls and receives its token.
    /// </summary>
    private async Task<(Printer printer, string token)> EnrolByCodeExchangeAsync(HSDbContext context,
                                                                                   int? teamId,
                                                                                   long userId)
    {
        PrusaConnectService service = NewService(context);

        await service.GetPrinterCode(PrinterRequest());

        PrusaConnectRegistration registration = await context.PrusaConnectRegistrations.SingleAsync();
        string code = registration.TemporaryCode;

        Printer printer = await service.ClaimPrinterAsync(code, "MK3.5", null, teamId, userId);
        string? token = await service.GetToken(code);

        token.Should().NotBeNull("the poll must issue a token once the code is claimed");

        return (printer, token!);
    }

    /// <summary>
    /// The USB-key channel end to end: a user provisions, then the printer makes first contact and is
    /// promoted into the enrolled table.
    /// </summary>
    private async Task<(Printer printer, string token)> EnrolByUsbKeyAsync(HSDbContext context,
                                                                            int? teamId,
                                                                            long userId)
    {
        (Printer printer, string token) = await NewService(context).ProvisionPrinterAsync("MK3.5", null, teamId, userId);

        AuthenticateResult firstContact = await AuthenticateAsync(HeaderFingerprint, token);
        firstContact.Succeeded.Should().BeTrue("USB-key first contact is what promotes the provisioning token");

        return (printer, token);
    }

    // ---------- the constants themselves ----------

    /// <summary>
    /// The premise the whole identity design rests on: the header form is a true prefix of the body
    /// form, because the firmware truncates one buffer rather than hashing twice.
    /// </summary>
    /// <remarks>
    /// Verified byte-for-byte against a real MK3.5 and against the 50-character value in
    /// <c>private-captures/websucket</c>. If this ever fails, prefix-based matching is invalid and the
    /// fix has to change shape, so it is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void TheHeaderFingerprintIsAPrefixOfTheBodyFingerprint()
    {
        HeaderFingerprint.Length.Should().Be(16, "FINGERPRINT_HDR_SIZE");
        BodyFingerprint.Length.Should().Be(50, "FINGERPRINT_SIZE");
        BodyFingerprint.Should().StartWith(HeaderFingerprint);
    }

    // ---------- single channel, no re-enrolment ----------

    /// <summary>
    /// A printer enrolled purely by code exchange must be able to authenticate afterwards.
    /// </summary>
    /// <remarks>
    /// The claim stores <c>registration.FingerPrint</c> - the 50-character body value - as the enrolled
    /// credential's key, but every subsequent request presents the 16-character header value, so the
    /// enrolled lookup misses and the printer is refused. Nothing about re-enrolment is involved: one
    /// printer, one channel, one enrolment. This also rules out the HTTP transport, since
    /// <c>BasicRequest</c> truncates identically.
    /// </remarks>
    [Fact]
    public async Task APrinterEnrolledByCodeExchangeCanAuthenticateAfterwards()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        (Printer printer, string token) = await EnrolByCodeExchangeAsync(context, team.TeamId, userId: 1);

        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue("the printer presents the header fingerprint on every request it ever makes");
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");
    }

    /// <summary>
    /// The USB-key channel's equivalent, as a control: it enrols from the header value, so it agrees
    /// with itself and works today. Pinned so a fix for the code-exchange side cannot regress it.
    /// </summary>
    [Fact]
    public async Task APrinterEnrolledByUsbKeyCanAuthenticateAfterwards()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        // Act
        (Printer printer, string token) = await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");
    }

    // ---------- re-enrolment: same channel ----------

    /// <summary>
    /// Reissuing an enrolled printer's USB-key token must leave it working on the new token.
    /// </summary>
    /// <remarks>
    /// This is the outage with the truncation removed from the picture entirely - both the enrolment
    /// and the reissue use the same 16-character key. The enrolled row holds the hash of the first
    /// token while the printer's flash has moved on to the second, so unless the handler will consider
    /// this printer's outstanding provisioning row there is no path back.
    /// </remarks>
    [Fact]
    public async Task ReissuingAnEnrolledPrintersUsbTokenKeepsItAuthenticating()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer printer, string original) = await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        // Act - the operator writes a fresh stick for the printer they already have
        string reissued = await NewService(context).RegenerateProvisioningTokenAsync(printer.Id, userId: 1);

        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, reissued);

        // Assert
        result.Succeeded.Should().BeTrue("the printer now holds the reissued token and nothing else");
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}",
            "a reissue rebinds the enrolment it was issued for, rather than starting a new one");

        AuthenticateResult withOld = await AuthenticateAsync(HeaderFingerprint, original);
        withOld.Succeeded.Should().BeFalse("the token on the discarded stick must stop working");

        await using HSDbContext verify = NewContext();
        (await verify.PrusaConnectProvisionings.AnyAsync(TestContext.Current.CancellationToken)).Should()
            .BeFalse("the reissued token is consumed by the rebind, exactly as first contact consumes one");
    }

    /// <summary>
    /// A token provisioned for a <em>different</em> printer never binds to an enrolled one, however
    /// valid that token is in its own right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the security half of the rebind. Provisioning again through
    /// <c>ProvisionPrinterAsync</c> mints a second printer, so its token belongs to that second row -
    /// and anyone with <c>CanManage</c> on a team of their own can mint one. If the handler matched a
    /// mismatched token against every outstanding provisioning row, writing such a stick onto someone
    /// else's printer would hand the provisioner a credential that authenticates as it.
    /// </para>
    /// <para>
    /// Failing closed is the deliberate answer: from the wire an operator's accidental duplicate is
    /// indistinguishable from a takeover attempt, so the remedy is to reissue against the printer that
    /// is already enrolled - which is <c>CanManage</c>-gated on the team that owns it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATokenProvisionedForADifferentPrinterDoesNotBindToAnEnrolledOne()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember owner = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer enrolled, string original) = await EnrolByUsbKeyAsync(context, owner.TeamId, userId: 1);

        // someone else provisions a printer entry of their own and writes that stick
        TeamMember other = await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);
        (Printer theirs, string theirToken) = await NewService(context)
            .ProvisionPrinterAsync("Mine now", null, other.TeamId, userId: 2);

        // Act
        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, theirToken);

        // Assert
        result.Succeeded.Should().BeFalse("a provisioning token is only ever valid for the printer it was issued for");

        await using HSDbContext verify = NewContext();

        PrusaConnectAuthenticationData credential = await verify.PrusaConnectAuthentication
            .SingleAsync(a => a.FingerPrintKey == HeaderFingerprint, TestContext.Current.CancellationToken);

        credential.PrinterId.Should().Be(enrolled.Id, "the enrolment must not have moved to the other printer");
        new TokenService().VerifyToken(original, credential.HashedToken).Should()
            .BeTrue("the rightful owner's token must be untouched by the attempt");

        (await verify.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken)).PrinterId.Should().Be(theirs.Id,
            "the unrelated provisioning token is left alone, not consumed by the failed attempt");
    }

    // ---------- re-enrolment: across channels ----------

    /// <summary>
    /// The live bug: a USB-enrolled printer taken through the code-exchange flow must not lose its
    /// connection.
    /// </summary>
    /// <remarks>
    /// Claiming mints a second <see cref="Printer"/> because the 50-character body value matches no
    /// enrolled row, and the printer overwrites its flash with the new token - after which the
    /// 16-character lookup still finds the original row, holding the original hash. Confirmed against
    /// real hardware on 2026-07-24: 20+ minutes of continuous <c>/p/ws</c> auth failures, no recovery
    /// without reloading the original USB settings.
    /// </remarks>
    [Fact]
    public async Task ClaimingAUsbEnrolledPrinterByCodeKeepsItAuthenticating()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        // Act - the same physical printer is put through Add Printer to Connect
        (Printer _, string codeToken) = await EnrolByCodeExchangeAsync(context, team.TeamId, userId: 1);

        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, codeToken);

        // Assert
        result.Succeeded.Should().BeTrue("the printer's flash now holds the code-exchange token");
    }

    /// <summary>
    /// Re-enrolling one physical printer must never leave two printer rows behind, whichever pair of
    /// channels it went through.
    /// </summary>
    [Fact]
    public async Task ReEnrollingAPrinterDoesNotCreateASecondPrinterRow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer first, string _) = await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        // Act
        (Printer second, string _) = await EnrolByCodeExchangeAsync(context, team.TeamId, userId: 1);

        // Assert
        second.Id.Should().Be(first.Id, "both enrolments describe the same physical printer");

        await using HSDbContext verify = NewContext();
        (await verify.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await verify.PrusaConnectAuthentication.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "one printer holds one enrolled credential");
    }

    // ---------- the CanManage gate on a known printer ----------

    /// <summary>
    /// Claiming a code for a printer that is already enrolled requires <c>CanManage</c> on the team
    /// that already owns it - front-panel access alone does not transfer a printer between teams.
    /// </summary>
    /// <remarks>
    /// The claimant here manages a team of their own, so the only thing that can refuse them is the
    /// permission check on the *existing* printer's team rather than a missing default team.
    /// </remarks>
    [Fact]
    public async Task ClaimingAnAlreadyEnrolledPrinterWithoutCanManageIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember owner = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        await EnrolByUsbKeyAsync(context, owner.TeamId, userId: 1);

        // a second user with a perfectly good team of their own, and no rights on the owner's
        TeamMember stranger = await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);

        PrusaConnectService service = NewService(context);
        await service.GetPrinterCode(PrinterRequest());

        PrusaConnectRegistration registration = await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(registration.TemporaryCode, "Mine now", null, stranger.TeamId, userId: 2);

        // Assert
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A refused claim must leave the printer exactly as it was - still enrolled, still authenticating
    /// on its original token.
    /// </summary>
    /// <remarks>
    /// This is what makes refusal a safe answer: the printer only overwrites its flash after
    /// <see cref="PrusaConnectService.GetToken"/> issues a token, and that cannot happen while the
    /// registration is unclaimed. The unauthorised user's attempt is a no-op the printer never sees.
    /// </remarks>
    [Fact]
    public async Task ARefusedClaimLeavesTheEnrolledPrinterWorking()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember owner = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer printer, string originalToken) = await EnrolByUsbKeyAsync(context, owner.TeamId, userId: 1);

        TeamMember stranger = await AddTeamAsync(context, userId: 2, canManage: true, isDefault: true);

        PrusaConnectService service = NewService(context);
        await service.GetPrinterCode(PrinterRequest());

        PrusaConnectRegistration registration = await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        string code = registration.TemporaryCode;

        // Act
        Func<Task> claim = () => service.ClaimPrinterAsync(code, "Mine now", null, stranger.TeamId, userId: 2);
        await claim.Should().ThrowAsync<TeamAccessDeniedException>();

        // Assert
        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, originalToken);

        result.Succeeded.Should().BeTrue("the printer never learned of the failed claim");
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");

        await using HSDbContext verify = NewContext();
        (await verify.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "a refused claim must not leave a printer behind");

        (await verify.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken)).PrinterId
            .Should().BeNull("the pending registration stays unclaimed, ready for someone who may claim it");
    }

    /// <summary>
    /// The same claim, by someone who does manage the owning team, links to the existing printer
    /// rather than duplicating it, and rotates the credential onto it.
    /// </summary>
    [Fact]
    public async Task ClaimingAnAlreadyEnrolledPrinterWithCanManageLinksToIt()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember owner = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer enrolled, string originalToken) = await EnrolByUsbKeyAsync(context, owner.TeamId, userId: 1);

        // Act
        (Printer claimed, string newToken) = await EnrolByCodeExchangeAsync(context, owner.TeamId, userId: 1);

        // Assert
        claimed.Id.Should().Be(enrolled.Id);

        AuthenticateResult withNew = await AuthenticateAsync(HeaderFingerprint, newToken);
        withNew.Succeeded.Should().BeTrue("the printer's flash holds the newly issued token");

        AuthenticateResult withOld = await AuthenticateAsync(HeaderFingerprint, originalToken);
        withOld.Succeeded.Should().BeFalse("the superseded token must stop working");
    }

    // ---------- guards the fix must not trade away ----------

    /// <summary>
    /// A genuinely wrong token, with nothing pending for the printer, is still refused.
    /// </summary>
    /// <remarks>
    /// The obvious way to fix re-enrolment - fall through to the provisioning scan whenever the
    /// enrolled token does not verify - must not turn into "any token gets a second chance". With no
    /// outstanding provisioning row there is nothing to fall through to, and this must stay a failure.
    /// </remarks>
    [Fact]
    public async Task AWrongTokenIsStillRejectedWhenNothingIsPendingForThePrinter()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        // Act
        AuthenticateResult result = await AuthenticateAsync(HeaderFingerprint, new TokenService().GenerateToken());

        // Assert
        result.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// A fingerprint that merely shares a prefix boundary with nothing in particular is still unknown -
    /// prefix matching must not degrade into "any fingerprint matches any row".
    /// </summary>
    [Fact]
    public async Task AnUnrelatedFingerprintIsStillUnknown()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        TeamMember team = await AddTeamAsync(context, userId: 1, canManage: true, isDefault: true);

        (Printer _, string token) = await EnrolByUsbKeyAsync(context, team.TeamId, userId: 1);

        // Act - a different printer's fingerprint, presenting a token that is valid for ours
        AuthenticateResult result = await AuthenticateAsync("TVOP6VP6ELL9KHBF", token);

        // Assert
        result.Succeeded.Should().BeFalse("the token is not a substitute for identity");
    }

    /// <summary>
    /// The scheme options never change here, so the monitor is a constant. Hand-rolled to match this
    /// project's no-mocking-framework style.
    /// </summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<PrusaConnectAuthenticationSchemeOptions>
    {
        private readonly PrusaConnectAuthenticationSchemeOptions _options = new();

        public PrusaConnectAuthenticationSchemeOptions CurrentValue => _options;

        public PrusaConnectAuthenticationSchemeOptions Get(string? name)
        {
            return _options;
        }

        public IDisposable? OnChange(Action<PrusaConnectAuthenticationSchemeOptions, string?> listener)
        {
            return null;
        }
    }
}
