using System.Data;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Homespool.Data;

namespace Homespool.Host.Services;

/// <summary>
/// Begins transactions on the request-scoped <see cref="HomespoolDbContext"/>, so a page or controller
/// can span writes made through several domain services (e.g. ASP.NET Core Identity's
/// <c>UserManager</c> plus <see cref="Accounts.TeamService"/>) atomically, without any of those services
/// needing to know about the others or about transaction boundaries themselves.
/// </summary>
/// <remarks>
/// The transaction is ambient across every consumer of the same scoped <see cref="HomespoolDbContext"/>
/// instance within the request. Commit once everything succeeds; letting the transaction go out of
/// scope uncommitted rolls back every write made through it.
/// </remarks>
public class UnitOfWork
{
    private readonly HomespoolDbContext _dbContext;

    public UnitOfWork(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// A transaction whose reads are the ones its writes are decided on: no other writer runs between
    /// them. For a check-then-act whose invariant crosses rows - "at least one administrator stays
    /// open" - where two requests passing the same check at once is the failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stated, not assumed.</b> SQLite serialises every writer, and this provider's plain
    /// transaction already takes the write lock on entry - but that is the provider's default, and a
    /// default is not a requirement the code can be read to have. Naming the level makes the
    /// requirement visible at the call site, and a provider unable to give it refuses rather than
    /// quietly running deferred.
    /// </para>
    /// <para>
    /// <b>What it costs.</b> The write lock is held from the first statement, so every other writer
    /// waits behind this transaction rather than only behind its writes. Keep what runs inside short.
    /// </para>
    /// </remarks>
    public Task<IDbContextTransaction> BeginSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }
}
