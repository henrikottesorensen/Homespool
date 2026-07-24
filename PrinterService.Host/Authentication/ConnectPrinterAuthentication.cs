using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Authentication;

public class PrusaConnectPrinterAuthenticationHandler : AuthenticationHandler<PrusaConnectAuthenticationSchemeOptions>
{
    private readonly PSDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly UnitOfWork _unitOfWork;

    public PrusaConnectPrinterAuthenticationHandler(PSDbContext dbContext,
                                                    TokenService tokenService,
                                                    UnitOfWork unitOfWork,
                                                    IOptionsMonitor<PrusaConnectAuthenticationSchemeOptions> options,
                                                    ILoggerFactory loggerFactory,
                                                    UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _unitOfWork = unitOfWork;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Headers missing? Do nothing.
        if (!Request.Headers.TryGetValue(Headers.Fingerprint, out StringValues fingerprint) ||
            !Request.Headers.TryGetValue(Headers.Token, out StringValues token) ||
            !Request.Headers.TryGetValue(Headers.UserAgentPrinter, out StringValues uaPrinter) ||
            !Request.Headers.TryGetValue(Headers.UserAgentVersion, out StringValues uaVersion))
        {
            Logger.LogInformation("PrusaConnect authentication failed, no headers supplied");
            return AuthenticateResult.NoResult();
        }

        // StringValues, not string: passing it straight into the LINQ query left EF Core unable to
        // bind it as a SQLite parameter ("No mapping exists from object type
        // Microsoft.Extensions.Primitives.StringValues to a known managed provider native type"),
        // a 500 rather than a clean auth failure.
        string fingerprintValue = fingerprint.ToString();
        string tokenValue = token.ToString();

        // Hot path: one indexed lookup that covers every enrolled printer, code-exchange and USB-key
        // alike. A provisioned printer that has already made first contact lives here too, so this is
        // the path all but its very first request takes.
        AuthenticateResult? enrolled = await AuthenticateEnrolledAsync(fingerprintValue, tokenValue);

        if (enrolled is not null)
        {
            return enrolled;
        }

        // No enrolled printer carries this fingerprint. It may be a pre-provisioned printer making
        // first contact: verify the token against the outstanding provisioning tokens and, on a
        // match, promote it into the enrolled table.
        return await BindProvisionedPrinterAsync(fingerprintValue, tokenValue);
    }

    /// <summary>
    /// Returns null when no enrolled row carries the fingerprint (so the caller falls through to the
    /// provisioning path); otherwise a definite Success or Fail. A wrong token against a known
    /// enrolled printer is a Fail, never a fall-through — there is no provisioning row for an already
    /// enrolled fingerprint.
    /// </summary>
    private async Task<AuthenticateResult?> AuthenticateEnrolledAsync(string fingerprint, string token)
    {
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication
            .Include(a => a.Printer)
            .SingleOrDefaultAsync(a => a.FingerPrint == fingerprint);

        if (auth is null)
        {
            return null;
        }

        if (auth.Printer is null)
        {
            // Structurally unreachable now PrinterId is a required FK, but fail closed rather than
            // dereference null if a row is ever left inconsistent.
            Logger.LogWarning("PrusaConnect authentication failed: enrolled credential {AuthId} has no printer.", auth.Id);
            return AuthenticateResult.Fail("Printer enrollment incomplete");
        }

        if (_tokenService.VerifyToken(token, auth.HashedToken))
        {
            return AuthenticateResult.Success(BuildTicket(auth.PrinterId, auth.Printer));
        }

        return AuthenticateResult.Fail("PrusaConnect invalid token.");
    }

    /// <summary>
    /// First contact for a USB-provisioned printer. The provisioning table holds only unbound tokens
    /// (binding deletes the row), expected to be a handful at most, so a per-row
    /// <see cref="TokenService.VerifyToken"/> is cheap; it cannot be pushed into SQL because the
    /// stored value is a salted PBKDF2 hash. A match is promoted into the enrolled table under a
    /// transaction, so every later request from this printer takes the hot path above.
    /// </summary>
    private async Task<AuthenticateResult> BindProvisionedPrinterAsync(string fingerprint, string token)
    {
        List<PrusaConnectProvisioning> pending = await _dbContext.PrusaConnectProvisionings
            .Include(p => p.Printer)
            .ToListAsync();

        foreach (PrusaConnectProvisioning candidate in pending)
        {
            if (_tokenService.VerifyToken(token, candidate.HashedToken))
            {
                return await PromoteProvisionedPrinterAsync(candidate, fingerprint, token);
            }
        }

        Logger.LogInformation("PrusaConnect authentication failed, fingerprint unknown.");
        return AuthenticateResult.Fail("Printer unknown");
    }

    private async Task<AuthenticateResult> PromoteProvisionedPrinterAsync(PrusaConnectProvisioning provisioning,
                                                                          string fingerprint,
                                                                          string token)
    {
        if (provisioning.Printer is null)
        {
            // Structurally unreachable: PrinterId is set at provisioning time, before this row exists.
            Logger.LogWarning("PrusaConnect authentication failed: provisioning token {TokenId} has no printer.", provisioning.Id);
            return AuthenticateResult.Fail("Printer provisioning incomplete");
        }

        Printer printer = provisioning.Printer;

        try
        {
            await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(Context.RequestAborted);

            await _dbContext.PrusaConnectAuthentication.AddAsync(new PrusaConnectAuthenticationData
            {
                PrinterId = provisioning.PrinterId,
                FingerPrint = fingerprint,
                // The hash is copied across as-is: the plaintext was shown once at provisioning and is
                // gone; the printer just proved it holds it by verifying above.
                HashedToken = provisioning.HashedToken,
                EnrolledAt = TimeProvider.System.GetUtcNow(),
            }, Context.RequestAborted);

            _dbContext.PrusaConnectProvisionings.Remove(provisioning);

            await _dbContext.SaveChangesAsync(Context.RequestAborted);
            await transaction.CommitAsync(Context.RequestAborted);

            Logger.LogInformation("Printer {PrinterId} enrolled via USB-key provisioning.", provisioning.PrinterId);

            return AuthenticateResult.Success(BuildTicket(provisioning.PrinterId, printer));
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent first contact from the same printer: the winner already
            // wrote the enrolled row (unique fingerprint) and removed this provisioning row. The
            // transaction has rolled back on the way out of the using block; clear the failed pending
            // changes and replay the hot path against the row the winner committed.
            _dbContext.ChangeTracker.Clear();

            AuthenticateResult? replay = await AuthenticateEnrolledAsync(fingerprint, token);

            if (replay is not null)
            {
                return replay;
            }

            Logger.LogWarning("PrusaConnect provisioning promotion for printer {PrinterId} failed and left no enrolled row.",
                provisioning.PrinterId);
            return AuthenticateResult.Fail("Printer unknown");
        }
    }

    private static AuthenticationTicket BuildTicket(int printerId, Printer printer)
    {
        // The claims-only ClaimsIdentity constructor leaves AuthenticationType null, so
        // Identity.IsAuthenticated stays false even though this scheme "succeeded" -
        // DenyAnonymousAuthorizationRequirement then rejects it as anonymous (403). The
        // authenticationType argument is what makes the identity actually count as authenticated.
        ClaimsPrincipal principal = new
        (
            new ClaimsIdentity(
            [
                new Claim(PsClaimTypes.PrinterId, $"{printerId}"),
                new Claim(PsClaimTypes.Owner, $"{printer.TeamId}"),
                new Claim(JwtClaimTypes.Name, $"{printer.Name}"),
            ], Authentication.Schemes.PrusaConnectPrinter)
        );

        return new AuthenticationTicket(principal, Authentication.Schemes.PrusaConnectPrinter);
    }
}
