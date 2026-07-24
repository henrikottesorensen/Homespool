using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <see cref="PrusaConnectRegistration.Id"/> is logged in its place, which correlates an issue
    /// with the later poll and claim without reproducing the secret. The logging happens after
    /// <see cref="PSDbContext.SaveChangesAsync(System.Threading.CancellationToken)"/> because the
    /// key is not assigned until the insert completes.
    /// </para>
    /// </remarks>
    public async Task<DTO.CodeResponseDTO> GetPrinterCode(DTO.RegisterPrinterRequestDTO printer)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset codeExpiry = now + _options.RegistrationCodeLifetime;
        PrusaConnectRegistration? registration = await _dbContext.PrusaConnectRegistrations
            .SingleOrDefaultAsync(a => a.FingerPrint == printer.FingerPrint);

        bool registered = false;
        bool renewed = false;

        if (registration is null)
        {
            EntityEntry<PrusaConnectRegistration> newRegistration = await _dbContext.PrusaConnectRegistrations.AddAsync(
                new PrusaConnectRegistration
                {
                    FingerPrint = printer.FingerPrint,
                    SerialNumber = printer.SerialNumber,
                    TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber),
                    TemporaryCodeExpiry = codeExpiry,
                    CreatedAt = now,
                });

            registration = newRegistration.Entity;
            registered = true;
        }
        else if (registration.TemporaryCodeExpiry < now)
        {
            registration.TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber);
            registration.TemporaryCodeExpiry = codeExpiry;

            renewed = true;
        }

        await _dbContext.SaveChangesAsync();

        if (registered)
        {
            _logger.LogInformation("PrusaConnect printer {SerialNumber} ({PrinterType}, firmware {Firmware}) "
                                   + "registered as {RegistrationId}; Connect code issued, expiring {CodeExpiry:o}.",
                                   printer.SerialNumber, printer.PrinterType, printer.Firmware, registration.Id, registration.TemporaryCodeExpiry);
        }
        else if (renewed)
        {
            _logger.LogInformation("PrusaConnect registration {RegistrationId} for printer {SerialNumber} "
                                   + "renewed its Connect code, expiring {CodeExpiry:o}.",
                                   registration.Id, printer.SerialNumber, registration.TemporaryCodeExpiry);
        }

        return new DTO.CodeResponseDTO
        {
            TemporaryCode = registration.TemporaryCode,
            Expires = registration.TemporaryCodeExpiry,
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
    /// On success this <b>materialises the enrolled credential</b> in
    /// <see cref="PrusaConnectAuthenticationData"/> and deletes the pending
    /// <see cref="PrusaConnectRegistration"/>. From then on the printer authenticates through the
    /// enrolled table's single fingerprint lookup, and a replay of the (now absent) code is an
    /// ordinary 404. A re-registration of an already-enrolled printer reaches
    /// <see cref="MaterialiseEnrolledCredentialAsync"/> with a fingerprint that already has an
    /// enrolled row; that row is updated in place rather than colliding on its unique index, rotating
    /// the credential onto the printer <see cref="ClaimPrinterAsync"/> already linked the claim to. A
    /// mainboard replacement changes the fingerprint itself and so still produces a second printer -
    /// that one is genuinely different hardware (AGENT-NOTES protocol-reference §570).
    /// </para>
    /// <para>
    /// <c>TemporaryCode</c> is deliberately non-uniquely indexed, so a collision yields more than one
    /// row rather than being impossible. <see cref="SingleOrDefaultAsync"/> throws in that case, which
    /// the controller surfaces as a 400 - honest, and vanishingly rare at 24 base36 characters.
    /// </para>
    /// <para>
    /// <b>Expiry is enforced here, in the query.</b> <see cref="GetPrinterCode"/> only replaces an
    /// expired code on the printer's next POST, so without this a code stayed redeemable indefinitely
    /// between expiring and being renewed. The predicate compares timestamps in SQL, which is only
    /// possible because <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> stores them as epoch
    /// milliseconds.
    /// </para>
    /// </remarks>
    public async Task<string?> GetToken(string temporaryCode)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        PrusaConnectRegistration? registration = await FindActiveRegistrationAsync(temporaryCode, now);

        if (registration is null)
        {
            throw PrinterNotFoundException.ForUnknownRegistrationCode();
        }

        if (registration.PrinterId is null)
        {
            // Still unclaimed. The registration is deliberately *not* consumed here: the printer polls
            // this endpoint repeatedly while it waits for a user, so consuming on contact rather than
            // on redemption would end registration on the first poll.
            return null;
        }

        string token = _tokenService.GenerateToken();

        await MaterialiseEnrolledCredentialAsync(registration.PrinterId.Value, registration.FingerPrint,
            _tokenService.HashToken(token), now);

        // The handshake this registration belonged to is over. Removing it - rather than leaving a
        // spent row - is what makes the code single-use: a replay finds nothing and is a 404, and it
        // cannot be renewed back to life the way an expired-but-present code could.
        _dbContext.PrusaConnectRegistrations.Remove(registration);

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
    private Task<PrusaConnectRegistration?> FindActiveRegistrationAsync(string temporaryCode, DateTimeOffset now)
    {
        return _dbContext.PrusaConnectRegistrations
            .SingleOrDefaultAsync(a => a.TemporaryCode == temporaryCode && a.TemporaryCodeExpiry > now);
    }

    /// <summary>
    /// Upserts the enrolled credential, keyed on the truncated fingerprint the printer will actually
    /// present on its later requests. Insert is the normal case; the update branch covers a
    /// re-enrollment of a printer that already has a row, where a plain insert would violate the
    /// enrolled table's unique index. Does not save — the caller owns the transaction.
    /// </summary>
    /// <remarks>
    /// <paramref name="fullFingerPrint"/> is the long form from <c>/p/register</c>'s body. It is
    /// recorded, but the key is derived from it: keying on the long form left the credential
    /// unreachable, since no later request ever carries it (see <see cref="PrinterFingerprint"/>).
    /// </remarks>
    private async Task MaterialiseEnrolledCredentialAsync(int printerId, string fullFingerPrint, string hashedToken, DateTimeOffset now)
    {
        string key = PrinterFingerprint.Key(fullFingerPrint);

        PrusaConnectAuthenticationData? existing = await _dbContext.PrusaConnectAuthentication
            .SingleOrDefaultAsync(a => a.FingerPrintKey == key);

        if (existing is null)
        {
            await _dbContext.PrusaConnectAuthentication.AddAsync(new PrusaConnectAuthenticationData
            {
                PrinterId = printerId,
                FingerPrintKey = key,
                FullFingerPrint = fullFingerPrint,
                HashedToken = hashedToken,
                EnrolledAt = now,
            });

            return;
        }

        existing.PrinterId = printerId;
        existing.FullFingerPrint = fullFingerPrint;
        existing.HashedToken = hashedToken;
        existing.EnrolledAt = now;
    }

    /// <summary>
    /// The app-facing half of the code-exchange claim: a signed-in user redeems the code the printer
    /// is displaying, creating the <see cref="Printer"/> row and linking it to the pending
    /// registration. Distinct from <see cref="GetToken"/> - the printer's own poll - which only starts
    /// succeeding once this has run.
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
    /// </remarks>
    public async Task<Printer> ClaimPrinterAsync(string temporaryCode, string? name, string? location, int? teamId, long userId)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        PrusaConnectRegistration? registration = await FindActiveRegistrationAsync(temporaryCode, now);

        if (registration is null)
        {
            throw PrinterNotFoundException.ForUnknownRegistrationCode();
        }

        if (registration.PrinterId is not null)
        {
            throw new RegistrationAlreadyClaimedException();
        }

        Printer? enrolled = await FindEnrolledPrinterAsync(registration.FingerPrint);

        if (enrolled is not null)
        {
            return await LinkClaimToEnrolledPrinterAsync(registration, enrolled, userId);
        }

        int resolvedTeamId = await ResolveTeamForWriteAsync(teamId, userId);

        Printer printer = NewPrinter(name, location, resolvedTeamId, now);

        await _dbContext.Printers.AddAsync(printer);

        // Assigning the navigation rather than PrinterId directly: the printer's Id isn't generated
        // until SaveChanges runs the insert, and EF fixes up the foreign key from this once it is.
        registration.Printer = printer;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("PrusaConnect registration {RegistrationId} claimed as printer {PrinterUuid} in team {TeamId}.",
            registration.Id, printer.Uuid, resolvedTeamId);

        return printer;
    }

    /// <summary>
    /// The printer already enrolled under this fingerprint, if there is one. The registration carries
    /// the long form from <c>/p/register</c>'s body; the enrolled table is keyed on the short form, so
    /// the comparison happens on the key both channels share.
    /// </summary>
    private async Task<Printer?> FindEnrolledPrinterAsync(string fullFingerPrint)
    {
        string key = PrinterFingerprint.Key(fullFingerPrint);

        PrusaConnectAuthenticationData? existing = await _dbContext.PrusaConnectAuthentication
            .Include(a => a.Printer)
            .SingleOrDefaultAsync(a => a.FingerPrintKey == key);

        return existing?.Printer;
    }

    /// <summary>
    /// Re-registering a printer that is already enrolled points the claim at the printer it already
    /// is, rather than minting a second one for the same hardware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Requires <c>CanManage</c> on the team that already owns it.</b> Reaching the printer's front
    /// panel is enough to start a re-registration, so without this check anyone who can walk up to a
    /// printer could take it over by claiming the code it displays. The permission is on the owning
    /// team, not on any team the claimant nominated - the printer does not move, and
    /// <paramref name="registration"/>'s requested name, location and team are deliberately ignored.
    /// </para>
    /// <para>
    /// Refusing here is safe for the printer: it overwrites its stored token only once
    /// <see cref="GetToken"/> has issued one, and that cannot happen while the registration is
    /// unclaimed. A refused claim leaves the pending row for someone who does hold the permission.
    /// </para>
    /// </remarks>
    private async Task<Printer> LinkClaimToEnrolledPrinterAsync(PrusaConnectRegistration registration, Printer enrolled, long userId)
    {
        await RequireManageAsync(enrolled.TeamId, userId);

        registration.PrinterId = enrolled.Id;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("PrusaConnect registration {RegistrationId} claimed for already-enrolled printer {PrinterUuid}; "
                               + "its credential will be replaced when the printer next polls.",
            registration.Id, enrolled.Uuid);

        return enrolled;
    }

    /// <summary>
    /// The USB-key enrollment channel: a signed-in user creates a <see cref="Printer"/> and a
    /// pre-provisioned token up front, to be written into <c>prusa_printer_settings.ini</c> on a USB
    /// stick. Returns the plaintext token once - only its hash is stored - so the caller can render
    /// the snippet. The printer never touches <c>/p/register</c>; it presents this token on its first
    /// request and the auth handler binds and promotes it there.
    /// </summary>
    public async Task<(Printer Printer, string Token)> ProvisionPrinterAsync(string? name, string? location, int? teamId, long userId)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        int resolvedTeamId = await ResolveTeamForWriteAsync(teamId, userId);

        Printer printer = NewPrinter(name, location, resolvedTeamId, now);
        await _dbContext.Printers.AddAsync(printer);

        string token = _tokenService.GenerateToken();

        await _dbContext.PrusaConnectProvisionings.AddAsync(new PrusaConnectProvisioning
        {
            // Navigation, not PrinterId: the id isn't assigned until the insert runs (see ClaimPrinterAsync).
            Printer = printer,
            HashedToken = _tokenService.HashToken(token),
            CreatedAt = now,
        });

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Printer {PrinterUuid} provisioned with a USB-key token in team {TeamId}.",
            printer.Uuid, resolvedTeamId);

        return (printer, token);
    }

    /// <summary>
    /// Issues a fresh pre-provisioned token for a printer that already exists here: one whose USB
    /// stick was never written or whose snippet was mistyped, and equally one that is already enrolled
    /// and needs a new credential written to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the supported way to re-provision a printer.</b> Provisioning again through
    /// <see cref="ProvisionPrinterAsync"/> would mint a <em>second</em> printer for the same hardware,
    /// and the printer would then present a token the enrolled row does not know - the auth handler
    /// deliberately refuses to bind that (it cannot tell an accident from a takeover attempt). Here the
    /// caller names the printer they mean and has proved <c>CanManage</c> on its team, so the new token
    /// can be bound to the existing enrollment on first contact.
    /// </para>
    /// <para>
    /// An enrolled printer keeps authenticating with its current token until the reissued one is
    /// actually presented: the enrolled credential is untouched here, and only the outstanding
    /// provisioning row carries the new hash. Writing a stick and never using it costs nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="PrinterNotFoundException">No printer with that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanManage</c> on the printer's team.</exception>
    /// <exception cref="ProvisioningTokenNotFoundException">
    /// The printer was never provisioned and is not enrolled — there is no enrollment for a reissued
    /// token to attach to.
    /// </exception>
    public async Task<string> RegenerateProvisioningTokenAsync(int printerId, long userId)
    {
        Printer? printer = await _dbContext.Printers.SingleOrDefaultAsync(p => p.Id == printerId);

        if (printer is null)
        {
            throw new PrinterNotFoundException($"Printer {printerId} was not found.");
        }

        await RequireManageAsync(printer.TeamId, userId);

        PrusaConnectProvisioning? provisioning = await _dbContext.PrusaConnectProvisionings
            .SingleOrDefaultAsync(p => p.PrinterId == printerId);

        string token = _tokenService.GenerateToken();

        if (provisioning is not null)
        {
            provisioning.HashedToken = _tokenService.HashToken(token);
        }
        else
        {
            // No outstanding row. Either the printer has already enrolled - first contact promoted its
            // token into the enrolled table and deleted this row - in which case a fresh row is exactly
            // what a reissue means; or it was never provisioned at all, and there is nothing to reissue
            // for.
            bool isEnrolled = await _dbContext.PrusaConnectAuthentication.AnyAsync(a => a.PrinterId == printerId);

            if (!isEnrolled)
            {
                throw new ProvisioningTokenNotFoundException();
            }

            await _dbContext.PrusaConnectProvisionings.AddAsync(new PrusaConnectProvisioning
            {
                PrinterId = printerId,
                HashedToken = _tokenService.HashToken(token),
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Provisioning token regenerated for printer {PrinterUuid}.", printer.Uuid);

        return token;
    }

    /// <summary>
    /// Which of <paramref name="printerIds"/> are enrolled (have authenticated at least once) versus
    /// still have an outstanding USB-key provisioning token awaiting first contact. A printer claimed
    /// through the code exchange but not yet polled by its own printer appears in neither set - there
    /// is nothing to show or act on for it here, only "waiting for the printer to connect".
    /// </summary>
    public async Task<PrinterEnrollmentStatus> GetEnrollmentStatusAsync(IReadOnlyCollection<int> printerIds, CancellationToken cancellationToken)
    {
        HashSet<int> enrolled = (await _dbContext.PrusaConnectAuthentication
            .Where(a => printerIds.Contains(a.PrinterId))
            .Select(a => a.PrinterId)
            .ToListAsync(cancellationToken)).ToHashSet();

        HashSet<int> awaitingProvisioning = (await _dbContext.PrusaConnectProvisionings
            .Where(p => printerIds.Contains(p.PrinterId))
            .Select(p => p.PrinterId)
            .ToListAsync(cancellationToken)).ToHashSet();

        return new PrinterEnrollmentStatus(enrolled, awaitingProvisioning);
    }

    private static Printer NewPrinter(string? name, string? location, int teamId, DateTimeOffset now) => new()
    {
        Uuid = Guid.NewGuid(),
        Type = PrinterType.PrusaConnect,
        TeamId = teamId,
        Name = name,
        Location = location,
        Status = PrinterStatus.Unknown,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Resolves the team a newly-added printer lands in. An explicit <paramref name="teamId"/> requires
    /// <c>CanManage</c> on it - adding a printer is treated as a structural change to the team, the
    /// same tier as inviting a member. Omitted, the printer lands in the caller's default team.
    /// </summary>
    private async Task<int> ResolveTeamForWriteAsync(int? teamId, long userId)
    {
        if (teamId is int explicitTeamId)
        {
            await RequireManageAsync(explicitTeamId, userId);
            return explicitTeamId;
        }

        TeamMember? defaultMembership = await _teamService.GetDefaultTeamMembershipAsync(userId, CancellationToken.None);

        // Should be unreachable: every account is given a default team at creation
        // (TeamProvisioning.AddDefaultTeam). Fail closed rather than create a teamless printer.
        if (defaultMembership is null)
        {
            throw new TeamAccessDeniedException();
        }

        return defaultMembership.TeamId;
    }

    private async Task RequireManageAsync(int teamId, long userId)
    {
        TeamMember? membership = await _teamService.GetMemberAsync(teamId, userId, CancellationToken.None);

        if (membership is null || !membership.CanManage)
        {
            throw new TeamAccessDeniedException();
        }
    }
}

/// <summary>See <see cref="PrusaConnectService.GetEnrollmentStatusAsync"/>.</summary>
public sealed record PrinterEnrollmentStatus(IReadOnlySet<int> Enrolled, IReadOnlySet<int> AwaitingUsbProvisioning);
