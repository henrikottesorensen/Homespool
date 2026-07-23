using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.PrusaConnect;

public class PrusaConnectService
{
    private readonly PSDbContext _dbContext;
    private readonly CodeGenerator _codeGenerator;
    private readonly TokenService _tokenService;
    private readonly TeamService _teamService;
    private readonly ILogger<PrusaConnectService> _logger;
    private readonly PrusaConnectOptions _options;

    public PrusaConnectService(PSDbContext dbContext,
                          CodeGenerator codeGenerator,
                          TokenService tokenService,
                          TeamService teamService,
                          ILogger<PrusaConnectService> logger,
                          IOptions<PrusaConnectOptions> options)
    {
        _dbContext = dbContext;
        _codeGenerator = codeGenerator;
        _tokenService = tokenService;
        _teamService = teamService;
        _logger = logger;
        _options = options.Value;
    }
    
    /// <summary>
    /// Issues a registration code for a printer, or renews it once the previous one has expired.
    /// </summary>
    /// <remarks>
    /// <b>The code itself is never logged.</b> It is a bearer credential - <see cref="GetToken"/>
    /// looks up by code and by nothing else, so whoever holds it can claim the printer until it
    /// expires. It used to be written at Information level on every issue and renewal, alongside a
    /// destructured <see cref="DTO.RegisterPrinterRequestDTO"/> that carried the fingerprint too,
    /// which put a live credential into the console sink and anything downstream of it.
    /// <para>
    /// <see cref="PrusaConnectAuthenticationData.Id"/> is logged in its place, which correlates an
    /// issue with the later poll and claim without reproducing the secret. The logging happens after
    /// <see cref="PSDbContext.SaveChangesAsync(System.Threading.CancellationToken)"/> because the
    /// key is not assigned until the insert completes.
    /// </para>
    /// </remarks>
    public async Task<DTO.CodeResponseDTO> GetPrinterCode(DTO.RegisterPrinterRequestDTO printer)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset codeExpiry = now + _options.RegistrationCodeLifetime;
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication.SingleOrDefaultAsync(a => a.FingerPrint == printer.FingerPrint);

        bool registered = false;
        bool renewed = false;

        if (auth is null)
        {
            EntityEntry<PrusaConnectAuthenticationData> newAuth = await _dbContext.PrusaConnectAuthentication.AddAsync(
                new PrusaConnectAuthenticationData
                {
                    FingerPrint = printer.FingerPrint,
                    SerialNumber = printer.SerialNumber,
                    TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber),
                    TemporaryCodeExpiry = codeExpiry,
                    CreatedAt = now,
                });

            auth = newAuth.Entity;
            registered = true;
        }
        else if (auth.TemporaryCodeExpiry < now)
        {
            auth.TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber);
            auth.TemporaryCodeExpiry = codeExpiry;

            renewed = true;
        }

        await _dbContext.SaveChangesAsync();

        if (registered)
        {
            _logger.LogInformation("PrusaConnect printer {SerialNumber} ({PrinterType}, firmware {Firmware}) "
                                   + "registered as {RegistrationId}; Connect code issued, expiring {CodeExpiry:o}.",
                                   printer.SerialNumber, printer.PrinterType, printer.Firmware, auth.Id, auth.TemporaryCodeExpiry);
        }
        else if (renewed)
        {
            _logger.LogInformation("PrusaConnect registration {RegistrationId} for printer {SerialNumber} "
                                   + "renewed its Connect code, expiring {CodeExpiry:o}.",
                                   auth.Id, printer.SerialNumber, auth.TemporaryCodeExpiry);
        }

        return new DTO.CodeResponseDTO
        {
            TemporaryCode = auth.TemporaryCode,
            Expires = auth.TemporaryCodeExpiry,
        };
    }

    /// <summary>
    /// Issues the real token once a user has claimed the printer, or null while it is still unclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Looked up by code, and only by code.</b> The poll is the one request where the code is the
    /// sole identifier available: Buddy's <c>PollRequest</c> sends nothing but a <c>Code</c> header.
    /// </para>
    /// <para>
    /// An optional fingerprint filter was tried and removed. It could not be a security control,
    /// because a caller opts into it by sending the header — anyone holding a stolen code simply omits
    /// it. And it applied only to the Python SDK, the one client that does send a fingerprint, so its
    /// sole possible effect was to reject a client that Buddy's path would have accepted.
    /// </para>
    /// <para>
    /// The previous version looked up by fingerprint and then compared the code in constant time,
    /// which is the better security shape but is not available to us: without a fingerprint there is
    /// nothing else to key on. The code is 24 base36 characters from <see cref="CodeGenerator"/>, so
    /// it is a credential in its own right and is treated as one.
    /// </para>
    /// <para>
    /// <c>TemporaryCode</c> is deliberately non-uniquely indexed, so a collision yields more than one
    /// row rather than being impossible. <see cref="SingleOrDefaultAsync"/> throws in that case, which
    /// the controller surfaces as a 400 - honest, and vanishingly rare at 24 base36 characters.
    /// </para>
    /// <para>
    /// <b>Expiry is enforced here, in the query.</b> <see cref="GetPrinterCode"/> only replaces an
    /// expired code on the printer's next POST, so without this a code stayed redeemable indefinitely
    /// between expiring and being renewed - which made
    /// <see cref="PrusaConnectOptions.RegistrationCodeLifetimeMinutes"/> control when a code was
    /// *replaced* rather than when it stopped working.
    /// </para>
    /// <para>
    /// The predicate compares timestamps in SQL, which is only possible because
    /// <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> stores them as epoch milliseconds.
    /// Against EF's default <see cref="DateTimeOffset"/> mapping the provider refuses to translate
    /// the comparison at all and throws at runtime.
    /// </para>
    /// <para>
    /// <b>The code is consumed once it is redeemed</b>, so it is single-use for the step that
    /// actually hands out a credential while staying reusable for the polling that precedes it.
    /// </para>
    /// <para>
    /// <b>This does not on its own stop a claimed printer being taken over.</b> A caller who knows
    /// the fingerprint can still POST to <see cref="GetPrinterCode"/>, be handed a fresh code for
    /// the existing registration, and redeem it here for a new token, because neither method checks
    /// whether <see cref="PrusaConnectAuthenticationData.HashedToken"/> is already set. Consuming
    /// the code closes the window on a code that has leaked; it does not close the handshake.
    /// </para>
    /// </remarks>
    public async Task<string?> GetToken(string temporaryCode)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        PrusaConnectAuthenticationData? auth = await FindActiveRegistrationAsync(temporaryCode, now);

        if (auth is null)
        {
            throw PrinterNotFoundException.ForUnknownRegistrationCode();
        }

        if (auth.PrinterId is null)
        {
            // Still unclaimed. The code is deliberately *not* consumed here: the printer polls this
            // endpoint repeatedly while it waits for a user, so consuming on contact rather than on
            // redemption would end registration on the first poll.
            return null;
        }

        string token = _tokenService.GenerateToken();
        auth.HashedToken = _tokenService.HashToken(token);
        auth.TokenCreatedAt = now;

        // Consume the code now that it has been redeemed: the handshake it belongs to is over, and
        // leaving it live let anyone else holding it mint a second token for the rest of its
        // lifetime - which also silently overwrote the hash and locked out the printer that had just
        // registered.
        //
        // Expiring it reuses the filter in the query above rather than adding a second rule that
        // could disagree with it. Blanking TemporaryCode would be worse than it looks: it is
        // non-nullable, so it would need a sentinel, and the empty string is one a caller can
        // actually send - the controller only rejects a missing Code header, not an empty one, so an
        // empty code would match every consumed row.
        auth.TemporaryCodeExpiry = now;

        await _dbContext.SaveChangesAsync();

        return token;
    }

    /// <summary>
    /// Looked up by code, filtering out expired rows in the same predicate <see cref="GetToken"/> and
    /// <see cref="ClaimPrinterAsync"/> both rely on, so the two callers cannot drift into disagreeing
    /// about what "still valid" means. <c>TemporaryCode</c> is deliberately non-uniquely indexed (see
    /// <see cref="GetToken"/>'s remarks), so a collision surfaces as <see cref="SingleOrDefaultAsync"/>
    /// throwing rather than silently picking a row.
    /// </summary>
    private Task<PrusaConnectAuthenticationData?> FindActiveRegistrationAsync(string temporaryCode, DateTimeOffset now)
    {
        return _dbContext.PrusaConnectAuthentication
            .SingleOrDefaultAsync(a => a.TemporaryCode == temporaryCode && a.TemporaryCodeExpiry > now);
    }

    /// <summary>
    /// The app-facing half of the claim: a signed-in user redeems the code the printer is displaying,
    /// creating the <see cref="Printer"/> row and linking it to the registration. Distinct from
    /// <see cref="GetToken"/> - the printer's own poll - which only starts succeeding once this has run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Does not consume the code.</b> Unlike <see cref="GetToken"/>, claiming isn't the credential
    /// exchange - the printer still has to poll <c>GET /p/register</c> and redeem the code itself to get
    /// its token. Consuming it here would strand a printer that hasn't polled since the claim.
    /// </para>
    /// <para>
    /// <b>Rejects a second claim of the same code</b> rather than silently overwriting the printer the
    /// first claim created - the concrete answer to the "concurrent claim" question AGENT-NOTES
    /// phase-1.5 §15 step 7 left open as "even 'last write wins' is fine".
    /// </para>
    /// <para>
    /// <paramref name="teamId"/> given names an existing team the caller must hold <c>CanManage</c> on
    /// - adding a printer is treated as a structural change to the team, the same tier as inviting a
    /// member. Omitted, the printer lands in the caller's default team.
    /// </para>
    /// </remarks>
    public async Task<Printer> ClaimPrinterAsync(string temporaryCode, string? name, string? location, int? teamId, long userId)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        PrusaConnectAuthenticationData? auth = await FindActiveRegistrationAsync(temporaryCode, now);

        if (auth is null)
        {
            throw PrinterNotFoundException.ForUnknownRegistrationCode();
        }

        if (auth.PrinterId is not null)
        {
            throw new RegistrationAlreadyClaimedException();
        }

        int resolvedTeamId;

        if (teamId is int explicitTeamId)
        {
            TeamMember? membership = await _teamService.GetMemberAsync(explicitTeamId, userId, CancellationToken.None);

            if (membership is null || !membership.CanManage)
            {
                throw new TeamAccessDeniedException();
            }

            resolvedTeamId = explicitTeamId;
        }
        else
        {
            TeamMember? defaultMembership = await _teamService.GetDefaultTeamMembershipAsync(userId, CancellationToken.None);

            // Should be unreachable: every account is given a default team at creation
            // (TeamProvisioning.AddDefaultTeam). Fail closed rather than create a teamless printer.
            if (defaultMembership is null)
            {
                throw new TeamAccessDeniedException();
            }

            resolvedTeamId = defaultMembership.TeamId;
        }

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = resolvedTeamId,
            Name = name,
            Location = location,
            Status = PrinterStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _dbContext.Printers.AddAsync(printer);

        // Assigning the navigation rather than PrinterId directly: the printer's Id isn't generated
        // until SaveChanges runs the insert, and EF fixes up the foreign key from this once it is.
        auth.Printer = printer;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("PrusaConnect registration {RegistrationId} claimed as printer {PrinterUuid} in team {TeamId}.",
            auth.Id, printer.Uuid, resolvedTeamId);

        return printer;
    }
}
