using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

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

    public PrusaConnectService(PSDbContext dbContext,
                          CodeGenerator codeGenerator,
                          TokenService tokenService,
                          ILogger<PrusaConnectService> logger)
    {
        _dbContext = dbContext;
        _codeGenerator = codeGenerator;
        _tokenService = tokenService;
        _logger = logger;
    }
    
    public async Task<DTO.CodeResponseDTO> GetPrinterCode(DTO.RegisterPrinterRequestDTO printer)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication.SingleOrDefaultAsync(a => a.FingerPrint == printer.FingerPrint);
        
        if (auth is null)
        {
            EntityEntry<PrusaConnectAuthenticationData> newAuth = await _dbContext.PrusaConnectAuthentication.AddAsync(
                new PrusaConnectAuthenticationData
                {
                    FingerPrint = printer.FingerPrint,
                    SerialNumber = printer.SerialNumber,
                    TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber),
                    TemporaryCodeExpiry = now.AddHours(1),
                    CreatedAt = TimeProvider.System.GetUtcNow(),
                });
            
            auth = newAuth.Entity;
            
            _logger.LogInformation("PrusaConnect printer {@Printer} asked for a Connect Code {TemporaryCode}", printer, auth.TemporaryCode);
        }
        else if (auth.TemporaryCodeExpiry < now)
        {
            auth.TemporaryCode = _codeGenerator.GenerateCode(printer.SerialNumber);
            auth.TemporaryCodeExpiry = now.AddHours(1);
            
            _logger.LogInformation("PrusaConnect printer {@Printer} asking for a Connect Code renewal {TemporaryCode}", printer, auth.TemporaryCode);
        }
        
        await _dbContext.SaveChangesAsync();

        return new DTO.CodeResponseDTO
        {
            TemporaryCode = auth.TemporaryCode,
            Date = auth.CreatedAt,
            Expires = auth.TemporaryCodeExpiry,
        };
    }

    public async Task<string?> GetToken(string fingerPrint, string temporaryCode)
    {
        PrusaConnectAuthenticationData? auth = await _dbContext.PrusaConnectAuthentication.SingleOrDefaultAsync(a => a.FingerPrint == fingerPrint);

        if (auth is null)
        {
            throw new PrinterNotFoundException(fingerPrint);
        }

        if (auth.PrinterId == null)
        {
            return null;
        }
        else if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(temporaryCode), 
                                                    Encoding.UTF8.GetBytes(auth.TemporaryCode)))
        {
            string token = _tokenService.GenerateToken();
            auth.HashedToken = _tokenService.HashToken(token);
            
            await _dbContext.SaveChangesAsync();
            
            return token;
        }

        throw new UnauthorizedAccessException();
    }
}
