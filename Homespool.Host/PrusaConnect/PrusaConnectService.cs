using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Exceptions;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect;

public class PrusaConnectService
{
    /// <summary>
    /// How many unexpired pending registrations one fingerprint may hold at once.
    /// </summary>
    /// <remarks>
    /// Every <c>POST /p/register</c> adds a row and the endpoint is anonymous, so without a bound one
    /// fingerprint is an unlimited number of rows. A real printer holds one, plus one for each time a
    /// person restarted registration or the printer rebooted inside a code's lifetime; four leaves
    /// that room. What happens at the cap is <see cref="MakeRoomForRegistrationAsync"/>'s business.
    /// </remarks>
    public const int MaxPendingRegistrationsPerFingerprint = 4;

    /// <summary>
    /// How long a pre-provisioned USB-key token stays usable after it is written. Past this the
    /// printer's first contact is refused, and the operator reissues to get a fresh one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A working day, because a stick is written and walked to a printer in one sitting or not at
    /// all.</b> What is left otherwise is an unbound credential nobody is watching, sitting in the
    /// database and on a stick in a drawer. Reissuing is one click and produces a new stick, so the
    /// recovery costs less than the exposure it removes.
    /// </para>
    /// <para>
    /// <b>Expiry stops a token authenticating; it does not delete the row, and must not.</b> A printer
    /// provisioned but never enrolled has no other record of itself, so removing the row would strand
    /// it - <see cref="RegenerateProvisioningTokenAsync"/> needs either an outstanding row or an
    /// enrolment to attach to, and provisioning again mints a <em>second</em> printer whose token the
    /// auth handler then deliberately refuses to bind. The row stays, stops working, and says so on
    /// the listing.
    /// </para>
    /// <para>
    /// <b>Not configuration</b>, for the same reason the drain timeout is not: the number encodes what
    /// provisioning physically is rather than a preference, and a deployment that wanted a longer one
    /// wants a different enrolment channel.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan ProvisioningTokenLifetime = TimeSpan.FromHours(8);

    private readonly HomespoolDbContext _dbContext;
    private readonly CodeGenerator _codeGenerator;
    private readonly TokenService _tokenService;
    private readonly TeamService _teamService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PrusaConnectService> _logger;
    private readonly PrusaConnectOptions _options;

    public PrusaConnectService(HomespoolDbContext dbContext,
                               CodeGenerator codeGenerator,
                               TokenService tokenService,
                               TeamService teamService,
                               TimeProvider timeProvider,
                               ILogger<PrusaConnectService> logger,
                               IOptionsMonitor<PrusaConnectOptions> options)
    {
        _dbContext = dbContext;
        _codeGenerator = codeGenerator;
        _tokenService = tokenService;
        _teamService = teamService;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.CurrentValue;
    }

    /// <summary>
    /// Issues a registration code for a printer: a pending row and a fresh code of its own for every
    /// POST, whatever is already pending for the same fingerprint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A code is never handed out twice.</b> The POST behind this is anonymous and names the printer
    /// by a fingerprint in its body, so answering a repeat with the code already pending would read the
    /// code on the printer's screen out to anyone who knows the fingerprint - and the poll is keyed on
    /// the code alone, so whoever polls faster after the owner's claim collects the token. Codes that
    /// coexist make a stranger's POST worth only a code nobody will type. Nothing changes on the wire:
    /// firmware POSTs once per registration attempt, keeps the <c>Code</c> header it was given, and
    /// polls with that and nothing else.
    /// </para>
    /// <para>
    /// <b>The cost, accepted:</b> a printer that reboots between its owner's claim and its next poll
    /// POSTs again, shows a new code, and the owner types that one. Returning the same code on a
    /// repeat covered that case and no other.
    /// </para>
    /// <para>
    /// <b>An expired code is not renewed; it just expires.</b> Firmware never POSTs again on its own,
    /// so a renewed code would reach a printer only by way of a person restarting registration, which
    /// mints a row anyway - and renewing in place kept whatever claim the row carried.
    /// <see cref="RegistrationRetentionService"/> removes what expires.
    /// </para>
    /// <para>
    /// <b>The code itself is never logged.</b> It is a bearer credential - <see cref="GetToken"/>
    /// looks up by code and by nothing else, so whoever holds it can collect the printer's token once
    /// it is claimed. <see cref="PrusaConnectRegistration.Id"/> is logged in its place, which
    /// correlates an issue with the later poll and claim without reproducing the secret. The logging
    /// happens after
    /// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/> because the
    /// key is not assigned until the insert completes. The fingerprint stays out of the log as well:
    /// it is what the enrolled printer is later recognised by.
    /// </para>
    /// <para>
    /// <b>What the printer said about itself goes through <see cref="LogText.Clean(string)"/> first.</b> This
    /// endpoint is anonymous, so the serial, the model and the firmware version are three strings a
    /// stranger chose; length is the only other rule they pass. Refusing the registration over a
    /// character would buy nothing - the same values are restated on the next <c>INFO</c> - and would
    /// cost a printer its enrolment.
    /// </para>
    /// </remarks>
    /// <exception cref="RegistrationLimitReachedException">
    /// The fingerprint is at <see cref="MaxPendingRegistrationsPerFingerprint"/> and every one of those
    /// registrations has been claimed.
    /// </exception>
    public async Task<DTO.CodeResponseDTO> GetPrinterCode(DTO.RegisterPrinterRequestDTO printer)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        long[] evicted = await MakeRoomForRegistrationAsync(printer, now);

        EntityEntry<PrusaConnectRegistration> added = await _dbContext.PrusaConnectRegistrations.AddAsync(
            new PrusaConnectRegistration
            {
                FingerPrint = printer.FingerPrint,
                SerialNumber = printer.SerialNumber,
                TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber),
                TemporaryCodeExpiry = now + _options.RegistrationCodeLifetime,
                CreatedAt = now,
            });

        PrusaConnectRegistration registration = added.Entity;

        await _dbContext.SaveChangesAsync();

        if (evicted.Length > 0)
        {
            _logger.LogInformation("PrusaConnect printer {SerialNumber} was at its limit of pending registrations; " +
                                   "dropped the oldest unclaimed: {RegistrationIds}.",
                                   LogText.Clean(printer.SerialNumber), evicted);
        }

        _logger.LogInformation("PrusaConnect printer {SerialNumber} ({PrinterType}, firmware {Firmware}) " +
                               "registered as {RegistrationId}; Connect code issued, expiring {CodeExpiry:o}.",
                               LogText.Clean(printer.SerialNumber),
                               LogText.Clean(printer.PrinterType),
                               LogText.Clean(printer.Firmware),
                               registration.Id,
                               registration.TemporaryCodeExpiry);

        return new DTO.CodeResponseDTO
        {
            TemporaryCode = registration.TemporaryCode,
            Expires = registration.TemporaryCodeExpiry,
        };
    }

    /// <summary>
    /// Holds <see cref="MaxPendingRegistrationsPerFingerprint"/> ahead of an insert: drops the
    /// fingerprint's expired rows, then its oldest unclaimed ones until the new row fits, and returns
    /// the ids of those. Does not save - the removals commit with the insert they make room for, or
    /// not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>At the cap the oldest unclaimed registration goes, rather than the POST being refused.</b>
    /// Both answers let somebody who knows a fingerprint get in the way; they differ in what it costs.
    /// Refusing would let a handful of POSTs per code lifetime, made in advance, keep the printer from
    /// registering at all. Evicting means the printer's own POST always gets a code, and knocking that
    /// code out takes a burst landed in the minute between the code appearing on the screen and its
    /// owner typing it. It is also the right answer to the honest way of reaching the cap - a person
    /// restarting registration several times - since firmware has abandoned every code but the newest.
    /// </para>
    /// <para>
    /// <b>A claimed registration is never evicted.</b> Its owner has typed the code and the printer is
    /// seconds from collecting the token; if a POST could remove it, a stranger could knock over any
    /// registration in flight. So when every row at the cap is claimed the POST is refused instead,
    /// which takes several signed-in claims nobody's printer came back for.
    /// </para>
    /// <para>
    /// Two POSTs racing each other can each see room and leave the fingerprint one over; the next POST
    /// trims it back, which is why the loop runs until the row fits rather than removing one.
    /// </para>
    /// </remarks>
    private async Task<long[]> MakeRoomForRegistrationAsync(DTO.RegisterPrinterRequestDTO printer, DateTimeOffset now)
    {
        List<PrusaConnectRegistration> pending = await _dbContext.PrusaConnectRegistrations
                                                                 .Where(a => a.FingerPrint == printer.FingerPrint)
                                                                 .OrderBy(a => a.Id)
                                                                 .ToListAsync();

        List<PrusaConnectRegistration> live = pending.Where(a => a.TemporaryCodeExpiry > now).ToList();

        int excess = live.Count - MaxPendingRegistrationsPerFingerprint + 1;

        List<PrusaConnectRegistration> evicted = live.Where(a => a.PrinterId is null)
                                                     .Take(Math.Max(excess, 0))
                                                     .ToList();

        if (evicted.Count < excess)
        {
            _logger.LogWarning("PrusaConnect printer {SerialNumber} was refused a Connect code: {PendingCount} claimed " +
                               "registrations are already pending for its fingerprint.",
                               LogText.Clean(printer.SerialNumber), live.Count);

            throw new RegistrationLimitReachedException();
        }

        // Expired rows are refused by every lookup already, and they are in hand: removing them here is
        // what makes the cap a statement about rows rather than about rows the sweep has not reached.
        _dbContext.PrusaConnectRegistrations.RemoveRange(pending.Except(live));
        _dbContext.PrusaConnectRegistrations.RemoveRange(evicted);

        return evicted.Select(a => a.Id).ToArray();
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
    /// that one is genuinely different hardware.
    /// </para>
    /// <para>
    /// <b>The fingerprint's other pending registrations go with it.</b> The printer has its token, so
    /// every other code issued for it is one no printer is waiting on: an abandoned attempt, or a
    /// stranger's. A claimed one is removed as well, and has to be - redeemed later it would rotate
    /// the credential out from under the printer that has just enrolled.
    /// </para>
    /// <para>
    /// <c>TemporaryCode</c> is deliberately non-uniquely indexed, so a collision yields more than one
    /// row rather than being impossible. <see cref="EntityFrameworkQueryableExtensions.SingleOrDefaultAsync{TSource}(System.Linq.IQueryable{TSource},System.Threading.CancellationToken)"/> throws in that case, which
    /// the controller surfaces as a 400 - honest, and vanishingly rare at the ten Crockford base32
    /// characters <see cref="CodeGenerator"/> issues, which is 2^50.
    /// </para>
    /// <para>
    /// <b>Expiry is enforced here, in the query.</b> Nothing else retires a code between its expiry
    /// and the next <see cref="RegistrationRetentionService"/> pass, so without this one would stay
    /// redeemable for up to an hour longer than it says. The predicate compares timestamps in SQL,
    /// which is only possible because <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> stores
    /// them as epoch milliseconds.
    /// </para>
    /// </remarks>
    /// <exception cref="PrinterNotFoundException">No unexpired registration carries this code.</exception>
    /// <exception cref="EnrolledCredentialMismatchException">
    /// The fingerprint is enrolled to a different printer than the one this registration was claimed
    /// as. No token is issued and the registration is removed.
    /// </exception>
    public async Task<string?> GetToken(string temporaryCode)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

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

        bool materialised = await MaterialiseEnrolledCredentialAsync(registration.PrinterId.Value, registration.FingerPrint,
                                                                     _tokenService.HashToken(token), now);

        // The handshake this registration belonged to is over either way. Removing it - rather than
        // leaving a spent row - is what makes the code single-use: a replay finds nothing and is a 404.
        _dbContext.PrusaConnectRegistrations.Remove(registration);

        if (!materialised)
        {
            // Removed rather than left to expire because it can never succeed, and firmware polls a
            // failing code every five seconds for as long as it is switched on.
            await _dbContext.SaveChangesAsync();

            _logger.LogWarning("PrusaConnect registration {RegistrationId} was claimed as printer {PrinterId}, but its fingerprint " +
                               "is enrolled to a different printer; no token issued and the registration removed.",
                               registration.Id, registration.PrinterId.Value);

            throw new EnrolledCredentialMismatchException();
        }

        List<PrusaConnectRegistration> siblings = await _dbContext.PrusaConnectRegistrations
                                                                  .Where(a => a.FingerPrint == registration.FingerPrint &&
                                                                              a.Id != registration.Id)
                                                                  .ToListAsync();

        _dbContext.PrusaConnectRegistrations.RemoveRange(siblings);

        await _dbContext.SaveChangesAsync();

        return token;
    }

    /// <summary>
    /// Looked up by code, filtering out expired rows in the same predicate <see cref="GetToken"/> and
    /// <see cref="ClaimPrinterAsync"/> both rely on, so the two callers cannot drift into disagreeing
    /// about what "still valid" means. <c>TemporaryCode</c> is deliberately non-uniquely indexed (see
    /// <see cref="GetToken"/>'s remarks), so a collision surfaces as <see cref="EntityFrameworkQueryableExtensions.SingleOrDefaultAsync{TSource}(System.Linq.IQueryable{TSource},System.Threading.CancellationToken)"/>
    /// throwing rather than silently picking a row.
    /// </summary>
    private Task<PrusaConnectRegistration?> FindActiveRegistrationAsync(string temporaryCode, DateTimeOffset now)
    {
        return _dbContext.PrusaConnectRegistrations
                         .SingleOrDefaultAsync(a => a.TemporaryCode == temporaryCode && a.TemporaryCodeExpiry > now);
    }

    /// <summary>
    /// Upserts the enrolled credential, keyed on the truncated fingerprint the printer will actually
    /// present on its later requests, and says whether it did. Insert is the normal case; the update
    /// branch covers a re-enrolment of a printer that already has a row, where a plain insert would
    /// violate the enrolled table's unique index. Does not save — the caller owns the transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="fullFingerPrint"/> is the long form from <c>/p/register</c>'s body. It is
    /// recorded, but the key is derived from it: keying on the long form left the credential
    /// unreachable, since no later request ever carries it (see <see cref="PrinterFingerprint"/>).
    /// </para>
    /// <para>
    /// <b>False, and nothing written, when the row belongs to a printer other than
    /// <paramref name="printerId"/>.</b> Permission to take over an enrolled printer is checked at
    /// claim time, by <see cref="LinkClaimToEnrolledPrinterAsync"/>, which also points the claim at
    /// that printer - so a claim that went through it arrives here naming the row's own printer. One
    /// naming another was made while the fingerprint was not enrolled, by somebody who was never asked
    /// about the printer it has been enrolled to since (a USB-key first contact in between is enough).
    /// Repointing the row would move that printer's credential, and with it the hardware, to whoever
    /// typed a code, on no check at all. The caller must not issue a token on false.
    /// </para>
    /// </remarks>
    private async Task<bool> MaterialiseEnrolledCredentialAsync(int printerId,
                                                                string fullFingerPrint,
                                                                string hashedToken,
                                                                DateTimeOffset now)
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

            return true;
        }

        if (existing.PrinterId != printerId)
        {
            return false;
        }

        existing.FullFingerPrint = fullFingerPrint;
        existing.HashedToken = hashedToken;
        existing.EnrolledAt = now;

        return true;
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
    /// first claim created, rather than letting the last write win.
    /// </para>
    /// </remarks>
    public async Task<Printer> ClaimPrinterAsync(string temporaryCode, string? name, string? location, Guid? teamUuid, Caller caller)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

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
            return await LinkClaimToEnrolledPrinterAsync(registration, enrolled, caller);
        }

        int resolvedTeamId = await ResolveTeamForWriteAsync(teamUuid, caller);

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
    private async Task<Printer> LinkClaimToEnrolledPrinterAsync(PrusaConnectRegistration registration,
                                                                Printer enrolled,
                                                                Caller caller)
    {
        await RequireManageAsync(enrolled.TeamId, caller);

        registration.PrinterId = enrolled.Id;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("PrusaConnect registration {RegistrationId} claimed for already-enrolled printer {PrinterUuid}; " +
                               "its credential will be replaced when the printer next polls.",
                               registration.Id, enrolled.Uuid);

        return enrolled;
    }

    /// <summary>
    /// The USB-key enrolment channel: a signed-in user creates a <see cref="Printer"/> and a
    /// pre-provisioned token up front, to be written into <c>prusa_printer_settings.ini</c> on a USB
    /// stick. Returns the plaintext token once - only its hash is stored - so the caller can render
    /// the snippet. The printer never touches <c>/p/register</c>; it presents this token on its first
    /// request and the auth handler binds and promotes it there.
    /// </summary>
    public async Task<(Printer printer, string token)> ProvisionPrinterAsync(string? name,
                                                                             string? location,
                                                                             Guid? teamUuid,
                                                                             Caller caller)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        int resolvedTeamId = await ResolveTeamForWriteAsync(teamUuid, caller);

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
    /// can be bound to the existing enrolment on first contact.
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
    /// The printer was never provisioned and is not enrolled — there is no enrolment for a reissued
    /// token to attach to.
    /// </exception>
    public async Task<string> RegenerateProvisioningTokenAsync(int printerId, Caller caller)
    {
        Printer? printer = await _dbContext.Printers.SingleOrDefaultAsync(p => p.Id == printerId);

        if (printer is null)
        {
            throw new PrinterNotFoundException($"Printer {printerId} was not found.");
        }

        await RequireManageAsync(printer.TeamId, caller);

        PrusaConnectProvisioning? provisioning = await _dbContext.PrusaConnectProvisionings
                                                                 .SingleOrDefaultAsync(p => p.PrinterId == printerId);

        string token = _tokenService.GenerateToken();

        if (provisioning is not null)
        {
            provisioning.HashedToken = _tokenService.HashToken(token);

            // CreatedAt is the row's age and the age is what expires it, so a reissue that left it
            // alone would hand out a token born older than it is - and, on a row already past
            // ProvisioningTokenLifetime, one that is expired before the stick is written. The row is
            // reused; the credential on it is new.
            provisioning.CreatedAt = _timeProvider.GetUtcNow();
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
                CreatedAt = _timeProvider.GetUtcNow(),
            });
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Provisioning token regenerated for printer {PrinterUuid}.", printer.Uuid);

        return token;
    }

    /// <summary>
    /// Which of <paramref name="printerIds"/> are enrolled (have authenticated at least once) versus
    /// still have an outstanding USB-key provisioning token awaiting first contact, and which of those
    /// tokens have expired. A printer claimed through the code exchange but not yet polled by its own
    /// printer appears in neither set - there is nothing to show or act on for it here, only "waiting
    /// for the printer to connect".
    /// </summary>
    /// <remarks>
    /// <b>An expired row stays in <c>AwaitingUsbProvisioning</c> as well as in its own set</b>, and
    /// that overlap is deliberate rather than sloppy: the listing gates the reissue button on being
    /// enrolled or awaiting, and a printer provisioned but never enrolled is neither once its token
    /// ages out - so dropping it from that set would hide the one control that recovers it. The
    /// expired set is what changes the badge, not what decides whether anything can be done.
    /// </remarks>
    public async Task<PrinterEnrolmentStatus> GetEnrolmentStatusAsync(IReadOnlyCollection<int> printerIds,
                                                                      CancellationToken cancellationToken)
    {
        HashSet<int> enrolled = (await _dbContext.PrusaConnectAuthentication
                                                 .Where(a => printerIds.Contains(a.PrinterId))
                                                 .Select(a => a.PrinterId)
                                                 .ToListAsync(cancellationToken)).ToHashSet();

        HashSet<int> awaitingProvisioning = (await _dbContext.PrusaConnectProvisionings
                                                             .Where(p => printerIds.Contains(p.PrinterId))
                                                             .Select(p => p.PrinterId)
                                                             .ToListAsync(cancellationToken)).ToHashSet();

        DateTimeOffset issuedAfter = _timeProvider.GetUtcNow() - ProvisioningTokenLifetime;

        // A second read rather than one that carries the flag, because the flag cannot be expressed
        // without an anonymous type and the query is an indexed hit on a table with one row per
        // printer. Both are the same page load.
        HashSet<int> expiredProvisioning = (await _dbContext.PrusaConnectProvisionings
                                                            .Where(p => printerIds.Contains(p.PrinterId) &&
                                                                        p.CreatedAt <= issuedAfter)
                                                            .Select(p => p.PrinterId)
                                                            .ToListAsync(cancellationToken)).ToHashSet();

        return new PrinterEnrolmentStatus(enrolled, awaitingProvisioning, expiredProvisioning);
    }

    private static Printer NewPrinter(string? name, string? location, int teamId, DateTimeOffset now)
    {
        return new()
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
    }

    /// <summary>
    /// Resolves the team a newly-added printer lands in, and refuses a caller who may not put one
    /// there. Adding a printer is a structural change to the team, the same tier as inviting a member,
    /// so it needs <see cref="Capability.ManagePrinter"/> on both the credential and the membership -
    /// whether the team is named in <paramref name="teamUuid"/> or taken as the caller's default.
    /// </summary>
    /// <remarks>
    /// <b>Both branches end in <see cref="RequireManage"/>, and that is the point.</b> A caller
    /// who names no team has named no permitted one either, so the default team is not exempt from the
    /// check the explicit branch runs. Its membership is Manager at creation
    /// (<c>TeamProvisioning.AddDefaultTeam</c>) and nothing today can lower it or make a weaker
    /// membership default - but that is an invariant of the code writing memberships, not of this
    /// method, and a member editor would inherit it.
    /// </remarks>
    private async Task<int> ResolveTeamForWriteAsync(Guid? teamUuid, Caller caller)
    {
        if (teamUuid is Guid namedTeam)
        {
            TeamMember? named = await _teamService.GetMemberAsync(namedTeam, caller.UserId, CancellationToken.None);
            RequireManage(named, caller);
            return named!.TeamId;
        }

        TeamMember? defaultMembership = await _teamService.GetDefaultTeamMembershipAsync(caller.UserId, CancellationToken.None);

        // Should be unreachable: every account is given a default team at creation
        // (TeamProvisioning.AddDefaultTeam). Fail closed rather than create a teamless printer.
        if (defaultMembership is null)
        {
            throw new TeamAccessDeniedException();
        }

        RequireManage(defaultMembership, caller);

        return defaultMembership.TeamId;
    }

    private async Task RequireManageAsync(int teamId, Caller caller)
    {
        RequireManage(await _teamService.GetMemberAsync(teamId, caller.UserId, CancellationToken.None), caller);
    }

    private static void RequireManage(TeamMember? membership, Caller caller)
    {
        // Told apart, so a narrowed token is not mistaken for missing team access.
        if (!caller.Allows(Capability.ManagePrinter))
        {
            throw CredentialScopeDeniedException.For(Capability.ManagePrinter);
        }

        // A team that does not exist and a team the caller is not in both arrive here as null, and get
        // the same refusal.
        if (membership is null || !CapabilitySet.Parse(membership.Capabilities).Allows(Capability.ManagePrinter))
        {
            throw new TeamAccessDeniedException();
        }
    }
}

/// <summary>See <see cref="PrusaConnectService.GetEnrolmentStatusAsync"/>.</summary>
public sealed record PrinterEnrolmentStatus(IReadOnlySet<int> Enrolled,
                                            IReadOnlySet<int> AwaitingUsbProvisioning,
                                            IReadOnlySet<int> ExpiredUsbProvisioning);
