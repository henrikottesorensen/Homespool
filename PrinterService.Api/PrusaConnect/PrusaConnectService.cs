using System;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PrinterService.Api.Exceptions;
using PrinterService.Data;
using PrinterService.Model.Entities;

namespace PrinterService.Api.PrusaConnect;

public class PrusaConnectService
{
    private readonly PSDbContext _dbContext;
    private readonly CodeGenerator _codeGenerator;
    private readonly TokenService _tokenService;
    private readonly ILogger<PrusaConnectService> _logger;
    private readonly PrusaConnectOptions _options;

    public PrusaConnectService(PSDbContext dbContext,
                          CodeGenerator codeGenerator,
                          TokenService tokenService,
                          ILogger<PrusaConnectService> logger,
                          IOptions<PrusaConnectOptions> options)
    {
        _dbContext = dbContext;
        _codeGenerator = codeGenerator;
        _tokenService = tokenService;
        _logger = logger;
        _options = options.Value;
    }
    
    public async Task<DTO.CodeResponseDTO> GetPrinterCode(DTO.RegisterPrinterRequestDTO printer)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset codeExpiry = now + _options.RegistrationCodeLifetime;
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication.SingleOrDefaultAsync(a => a.FingerPrint == printer.FingerPrint);
        
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
            
            _logger.LogInformation("PrusaConnect printer {@Printer} asked for a Connect Code {TemporaryCode}", printer, auth.TemporaryCode);
        }
        else if (auth.TemporaryCodeExpiry < now)
        {
            auth.TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber);
            auth.TemporaryCodeExpiry = codeExpiry;
            
            _logger.LogInformation("PrusaConnect printer {@Printer} asking for a Connect Code renewal {TemporaryCode}", printer, auth.TemporaryCode);
        }
        
        await _dbContext.SaveChangesAsync();

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
    /// </remarks>
    public async Task<string?> GetToken(string temporaryCode)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // An expired code is treated as no code at all: filtered out here rather than found and then
        // rejected, so expiry and non-existence take the identical path and produce the identical 404.
        // Nothing distinguishes "wrong code" from "code you waited too long to use", which is also the
        // right answer for someone holding a credential they should no longer hold.
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication
            .SingleOrDefaultAsync(a => a.TemporaryCode == temporaryCode && a.TemporaryCodeExpiry > now);

        if (auth is null)
        {
            throw PrinterNotFoundException.ForUnknownRegistrationCode();
        }

        if (auth.PrinterId is null)
        {
            return null;
        }

        string token = _tokenService.GenerateToken();
        auth.HashedToken = _tokenService.HashToken(token);

        await _dbContext.SaveChangesAsync();

        return token;
    }
}
