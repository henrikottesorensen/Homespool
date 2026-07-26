using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// The printer-facing authentication handler, across both enrollment channels: the hot path against
/// the enrolled credential, and the USB-key first contact that binds a pre-provisioned token and
/// promotes it into that same enrolled table.
/// </summary>
/// <remarks>
/// Run against real SQLite, because the promotion runs inside a transaction and its safety rests on
/// the unique fingerprint index actually being enforced.
/// </remarks>
public sealed class PrusaConnectPrinterAuthenticationHandlerTests : IDisposable
{
    /// <summary>
    /// The fingerprint as it arrives on a header - the truncated 16-character form, which is the only
    /// one an authenticated request ever carries and therefore the only one this handler sees. The
    /// full 50-character form belongs to <c>/p/register</c>'s body; using it here would test a request
    /// no printer makes. See <c>PrinterFingerprint</c> and <c>PrusaConnectFingerprintIdentityTests</c>.
    /// </summary>
    private const string Fingerprint = "SUDBAJQ78CTJBNA8";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-auth-{Guid.NewGuid():N}.db");

    private static async Task<Printer> AddPrinterAsync(HSDbContext context)
    {
        Team team = new()
        {
            CreatedBy = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync();

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Name = "Bench printer",
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        return printer;
    }

    /// <summary>Seeds an already-enrolled printer and returns its plaintext token.</summary>
    private static async Task<(Printer printer, string token)> AddEnrolledPrinterAsync(HSDbContext context, string fingerprint)
    {
        Printer printer = await AddPrinterAsync(context);

        TokenService tokenService = new();
        string token = tokenService.GenerateToken();

        context.PrusaConnectAuthentication.Add(new PrusaConnectAuthenticationData
        {
            PrinterId = printer.Id,
            FingerPrintKey = fingerprint,
            HashedToken = tokenService.HashToken(token),
            EnrolledAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return (printer, token);
    }

    /// <summary>Seeds a printer with an outstanding, unbound provisioning token.</summary>
    private static async Task<(Printer printer, string token)> AddProvisionedPrinterAsync(HSDbContext context)
    {
        Printer printer = await AddPrinterAsync(context);

        TokenService tokenService = new();
        string token = tokenService.GenerateToken();

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = tokenService.HashToken(token),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return (printer, token);
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
        await context.Database.MigrateAsync();

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

    /// <summary>
    /// Drives one request end to end through the handler, exactly as the authentication middleware
    /// would: a fresh context (a request's own scope) and a fresh handler, since the base class
    /// memoises the result per request.
    /// </summary>
    private async Task<AuthenticateResult> AuthenticateAsync(HSDbContext context,
                                                             string? fingerprint,
                                                             string? token,
                                                             bool sendUserAgent = true)
    {
        DefaultHttpContext httpContext = new();

        if (fingerprint is not null)
        {
            httpContext.Request.Headers[Headers.Fingerprint] = fingerprint;
        }

        if (token is not null)
        {
            httpContext.Request.Headers[Headers.Token] = token;
        }

        if (sendUserAgent)
        {
            httpContext.Request.Headers[Headers.UserAgentPrinter] = "MK3.5";
            httpContext.Request.Headers[Headers.UserAgentVersion] = "6.4.0+11974";
        }

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

    // ---------- headers ----------

    /// <summary>
    /// A request with no printer headers is not this scheme's business - NoResult, not a failure, so
    /// other schemes still get their turn.
    /// </summary>
    [Fact]
    public async Task ARequestWithoutThePrinterHeadersYieldsNoResult()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, fingerprint: null, token: null);

        // Assert
        result.None.Should().BeTrue();
    }

    // ---------- the enrolled hot path ----------

    /// <summary>
    /// An enrolled printer authenticates, and the ticket carries the identifiers downstream needs:
    /// the printer id the controller threads to the WebSocket handler, and the owning team.
    /// </summary>
    [Fact]
    public async Task AnEnrolledPrinterAuthenticatesAndCarriesItsIdentityInTheTicket()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (Printer printer, string token) = await AddEnrolledPrinterAsync(context, Fingerprint);

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue("an unauthenticated identity is rejected as anonymous");
        result.Principal.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");
        result.Principal.FindFirst(HSClaimTypes.Owner)!.Value.Should().Be($"{printer.TeamId}");
    }

    /// <summary>
    /// A wrong token against a known fingerprint fails, and does not fall through to the provisioning
    /// scan - an enrolled fingerprint has no outstanding provisioning token by construction.
    /// </summary>
    [Fact]
    public async Task AnEnrolledPrinterWithTheWrongTokenIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await AddEnrolledPrinterAsync(context, Fingerprint);

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, new TokenService().GenerateToken());

        // Assert
        result.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// A fingerprint nothing has enrolled, with no provisioning token to match, is refused.
    /// </summary>
    [Fact]
    public async Task AnUnknownFingerprintWithNothingProvisionedIsRejected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, "no-such-fingerprint", new TokenService().GenerateToken());

        // Assert
        result.Succeeded.Should().BeFalse();
    }

    // ---------- USB-key first contact ----------

    /// <summary>
    /// First contact: a pre-provisioned printer presents a fingerprint the server has never seen plus
    /// the token from its USB stick, and is enrolled on the spot.
    /// </summary>
    /// <remarks>
    /// The fingerprint cannot be known in advance - it is derived from the mainboard - so binding it
    /// at first contact is what ties the pre-provisioned credential to a specific physical printer.
    /// </remarks>
    [Fact]
    public async Task FirstContactWithAProvisionedTokenEnrollsThePrinter()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (Printer printer, string token) = await AddProvisionedPrinterAsync(context);

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");

        await using HSDbContext verify = NewContext();

        PrusaConnectAuthenticationData enrolled = await verify.PrusaConnectAuthentication.SingleAsync();
        enrolled.PrinterId.Should().Be(printer.Id);
        enrolled.FingerPrintKey.Should().Be(Fingerprint, "the presented fingerprint is bound to the printer");

        (await verify.PrusaConnectProvisionings.AnyAsync()).Should()
            .BeFalse("the provisioning token is consumed by the promotion");
    }

    /// <summary>
    /// The promoted credential is the same secret the USB stick carries, so the printer keeps
    /// authenticating with the token it already has.
    /// </summary>
    [Fact]
    public async Task ThePromotedCredentialStillVerifiesAgainstTheProvisionedToken()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (Printer _, string token) = await AddProvisionedPrinterAsync(context);

        // Act
        await AuthenticateAsync(context, Fingerprint, token);

        // Assert
        await using HSDbContext verify = NewContext();
        PrusaConnectAuthenticationData enrolled = await verify.PrusaConnectAuthentication.SingleAsync();

        new TokenService().VerifyToken(token, enrolled.HashedToken).Should().BeTrue();
    }

    /// <summary>
    /// <b>The regression this design exists for.</b> Once a provisioned printer has enrolled, every
    /// later request is served by the enrolled table - it does not keep authenticating through the
    /// provisioning table forever.
    /// </summary>
    /// <remarks>
    /// Proven by emptying the provisioning table's contribution entirely: after first contact there is
    /// no provisioning row left at all, so a second successful authentication can only have come from
    /// the enrolled credential.
    /// </remarks>
    [Fact]
    public async Task AfterFirstContactThePrinterAuthenticatesFromTheEnrolledTableAlone()
    {
        // Arrange
        await using HSDbContext first = await MigratedContextAsync();
        (Printer printer, string token) = await AddProvisionedPrinterAsync(first);

        (await AuthenticateAsync(first, Fingerprint, token)).Succeeded.Should().BeTrue();

        // Act - a second request, in its own scope, exactly as the next real request would be
        await using HSDbContext second = NewContext();
        AuthenticateResult result = await AuthenticateAsync(second, Fingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");

        (await second.PrusaConnectProvisionings.AnyAsync()).Should()
            .BeFalse("nothing remains in the provisioning table to have served this request");
    }

    /// <summary>
    /// A token that matches no outstanding provisioning row is refused, and binds nothing.
    /// </summary>
    /// <remarks>
    /// The important half is that a failed attempt leaves the provisioning token outstanding: a wrong
    /// guess must not consume the real printer's pending enrollment.
    /// </remarks>
    [Fact]
    public async Task AWrongTokenAtFirstContactBindsNothing()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await AddProvisionedPrinterAsync(context);

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, new TokenService().GenerateToken());

        // Assert
        result.Succeeded.Should().BeFalse();

        await using HSDbContext verify = NewContext();
        (await verify.PrusaConnectAuthentication.AnyAsync()).Should().BeFalse("nothing was enrolled");
        (await verify.PrusaConnectProvisionings.CountAsync()).Should().Be(1, "the real printer's token must survive a wrong guess");
    }

    /// <summary>
    /// With several printers awaiting first contact, a token binds to its own printer and leaves the
    /// others outstanding.
    /// </summary>
    [Fact]
    public async Task AProvisionedTokenBindsOnlyItsOwnPrinter()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();

        await AddProvisionedPrinterAsync(context);
        (Printer target, string token) = await AddProvisionedPrinterAsync(context);
        await AddProvisionedPrinterAsync(context);

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, token);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{target.Id}");

        await using HSDbContext verify = NewContext();
        (await verify.PrusaConnectProvisionings.CountAsync()).Should().Be(2, "the other two are untouched");
        (await verify.PrusaConnectAuthentication.SingleAsync()).PrinterId.Should().Be(target.Id);
    }

    /// <summary>
    /// The enrolled credential wins over a stale provisioning row for the same printer - the state a
    /// request that lost the first-contact race observes when it replays.
    /// </summary>
    [Fact]
    public async Task AnEnrolledCredentialTakesPrecedenceOverALeftoverProvisioningRow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (Printer printer, string enrolledToken) = await AddEnrolledPrinterAsync(context, Fingerprint);

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        // Act
        AuthenticateResult result = await AuthenticateAsync(context, Fingerprint, enrolledToken);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(HSClaimTypes.PrinterId)!.Value.Should().Be($"{printer.Id}");
    }

    /// <summary>
    /// Two simultaneous first contacts from the same printer settle on one enrollment.
    /// </summary>
    /// <remarks>
    /// Whichever ordering the two requests happen to take, the invariants must hold: the unique
    /// fingerprint index admits exactly one enrolled row, the provisioning token is consumed exactly
    /// once, and neither request escapes as an unhandled exception - the loser rolls back and replays
    /// against the winner's row rather than surfacing a 500.
    /// </remarks>
    [Fact]
    public async Task ConcurrentFirstContactsSettleOnASingleEnrollment()
    {
        // Arrange
        await using HSDbContext seed = await MigratedContextAsync();
        (Printer printer, string token) = await AddProvisionedPrinterAsync(seed);

        await using HSDbContext left = NewContext();
        await using HSDbContext right = NewContext();

        // Act - two independent request scopes racing, as two real requests would
        AuthenticateResult[] results = await Task.WhenAll(
            AuthenticateAsync(left, Fingerprint, token),
            AuthenticateAsync(right, Fingerprint, token));

        // Assert
        results.Should().Contain(r => r.Succeeded, "at least one request must complete the enrollment");

        await using HSDbContext verify = NewContext();

        PrusaConnectAuthenticationData enrolled = await verify.PrusaConnectAuthentication.SingleAsync();
        enrolled.PrinterId.Should().Be(printer.Id);
        enrolled.FingerPrintKey.Should().Be(Fingerprint);

        (await verify.PrusaConnectProvisionings.AnyAsync()).Should().BeFalse("the token is consumed exactly once");
    }

    /// <summary>
    /// The scheme options never change here, so the monitor is a constant. Hand-rolled to match this
    /// project's no-mocking-framework style.
    /// </summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<PrusaConnectAuthenticationSchemeOptions>
    {
        private readonly PrusaConnectAuthenticationSchemeOptions _options = new();

        public PrusaConnectAuthenticationSchemeOptions CurrentValue => _options;

        public PrusaConnectAuthenticationSchemeOptions Get(string? name) => _options;

        public IDisposable? OnChange(Action<PrusaConnectAuthenticationSchemeOptions, string?> listener) => null;
    }
}
