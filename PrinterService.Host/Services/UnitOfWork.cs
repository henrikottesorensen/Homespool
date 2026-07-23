using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore.Storage;

using PrinterService.Data;

namespace PrinterService.Host.Services;

/// <summary>
/// Begins transactions on the request-scoped <see cref="PSDbContext"/>, so a page or controller
/// can span writes made through several domain services (e.g. ASP.NET Core Identity's
/// <c>UserManager</c> plus <see cref="TeamService"/>) atomically, without any of those services
/// needing to know about the others or about transaction boundaries themselves.
/// </summary>
/// <remarks>
/// The transaction is ambient across every consumer of the same scoped <see cref="PSDbContext"/>
/// instance within the request. Commit once everything succeeds; letting the transaction go out of
/// scope uncommitted rolls back every write made through it.
/// </remarks>
public class UnitOfWork
{
    private readonly PSDbContext _dbContext;

    public UnitOfWork(PSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }
}
