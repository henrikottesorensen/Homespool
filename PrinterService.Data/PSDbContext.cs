using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using PrinterService.Model;

namespace PrinterService.Data;

public class PSDbContext : DbContext, IDataProtectionKeyContext
{
    public DbSet<Printer> Printers { get; set; }
    
    public DbSet<PrusaConnectAuthenticationData> PrusaConnectAuthentication { get; set; }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    public PSDbContext(DbContextOptions<PSDbContext> options)
        : base(options)
    {
    }
}
