using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;

namespace Homespool.Host.Health;

/// <summary>
/// Who health alerts go to: every open administrator, re-read on every poll, and the last list read
/// kept for when the read fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Re-read every poll</b>, so closing an administrator stops their alerts, and an administrator's
/// new address takes over from the old one, within a poll. It is one indexed read a minute.
/// </para>
/// <para>
/// <b>A failed read keeps the previous list and does not throw.</b> The alert most worth sending is
/// the one about the database being unreachable, and that is exactly when this read fails - so the
/// outage is reported to whoever was an administrator at the last read that worked, rather than
/// the failure ending the poll before the alert is sent.
/// </para>
/// <para>
/// <b>The language is read in the same query as the address</b>, for the same reason: sending must
/// not touch the database. Looking it up per recipient at send time made every alert during an
/// outage fail on the lookup.
/// </para>
/// </remarks>
public sealed class AlertRecipients
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    private IReadOnlyList<AlertRecipient> _current = [];

    public AlertRecipients(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The list from the last read that worked; empty until one has.</summary>
    public IReadOnlyList<AlertRecipient> Current => _current;

    /// <summary>
    /// Re-reads the list. Never throws but for cancellation: a failure is logged and leaves
    /// <see cref="Current"/> as it was.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            var rows = await dbContext.Users
                                      .Where(user => Administrators.Open(dbContext).Contains(user.Id))
                                      .Select(user => new { user.Email, user.Language })
                                      .ToListAsync(cancellationToken);

            _current = rows.Where(row => !string.IsNullOrWhiteSpace(row.Email))
                           .Select(row => new AlertRecipient(row.Email!, SupportedLanguages.Resolve(row.Language)))
                           .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e,
                               "Could not re-read the health alert recipients; keeping the {RecipientCount} from the last read that worked.",
                               _current.Count);
        }
    }
}
