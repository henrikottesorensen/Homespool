using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Authentication;

public class PrusaConnectPrinterAuthenticationHandler : AuthenticationHandler<PrusaConnectAuthenticationSchemeOptions>
{
    private readonly PSDbContext _dbContext;
    private readonly TokenService _tokenService;

    public PrusaConnectPrinterAuthenticationHandler(PSDbContext dbContext,
                                                    TokenService tokenService,
                                                    IOptionsMonitor<PrusaConnectAuthenticationSchemeOptions> options,
                                                    ILoggerFactory loggerFactory,
                                                    UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
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

        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication
            .Include(prusaConnectAuthentication => prusaConnectAuthentication.Printer)
            .SingleOrDefaultAsync(p => p.FingerPrint == fingerprint);

        if (auth?.HashedToken is null)
        {
            Logger.LogInformation("PrusaConnect authentication failed, fingerprint unknown.");
            return AuthenticateResult.Fail("Printer unknown");
        }

        if (auth.Printer is null)
        {
            // Should be unreachable: GetToken only issues a token once PrinterId is set, so a hashed
            // token implies a claimed printer. If the pair ever comes apart the row is inconsistent,
            // and refusing to authenticate is safer than dereferencing null. AGENT-NOTES §3 recorded
            // this as a latent NPE while Printer was declared non-nullable; making it nullable turned
            // it into a compile error, which is how it got fixed.
            Logger.LogWarning("PrusaConnect authentication failed: registration {AuthId} has a token "
                              + "but no claimed printer.", auth.Id);

            return AuthenticateResult.Fail("Printer registration incomplete");
        }

        if (_tokenService.VerifyToken(token.ToString(), auth.HashedToken))
        {
            ClaimsPrincipal principal = new
            (
                new ClaimsIdentity(
                [
                    new Claim(PsClaimTypes.PrinterId, $"{auth.PrinterId}"),
                    new Claim(PsClaimTypes.Owner, $"{auth.Printer.TeamId}"),
                    new Claim(JwtClaimTypes.Name, $"{auth.Printer.Name}"),
                ])
            );

            return AuthenticateResult.Success(new AuthenticationTicket(principal,
                Authentication.Schemes.PrusaConnectPrinter));
        }

        return AuthenticateResult.Fail("PrusaConnect invalid token.");
    }
}
