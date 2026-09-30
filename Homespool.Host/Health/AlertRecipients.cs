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
using Homespool.Host.Notifications;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Health;

/// <summary>
/// Who health alerts go to: every open administrator with an address or a browser to reach, re-read on
/// every poll, and the last list read kept for when the read fails.
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
/// <para>
/// <b>So are the browsers</b>, whole, where every other notification reads them when it is sent -
/// and whether the administrator has turned health notifications off, which empties the list of
/// browsers and leaves the address. Without a mail server the address reaches nobody, so an
/// administrator who turns the switch off there hears nothing at all - the banner still shows it.
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
                                      .Select(user => new { user.Id, user.Email, user.Language, user.MutedNotifications })
                                      .ToListAsync(cancellationToken);

            List<long> ids = [.. rows.Select(row => row.Id)];

            ILookup<long, WebPushDestination> browsers = (await dbContext.WebPushDestinations
                                                                         .AsNoTracking()
                                                                         .Where(browser => ids.Contains(browser.UserId))
                                                                         .ToListAsync(cancellationToken))
                                                        .ToLookup(browser => browser.UserId);

            _current = rows.Select(row => new AlertRecipient(
                               row.Id,
                               string.IsNullOrWhiteSpace(row.Email) ? null : row.Email,
                               SupportedLanguages.Resolve(row.Language),
                               NotificationMutes.Parse(row.MutedNotifications).Contains(NotificationKind.ServiceHealth) ?
                                   [] :
                                   [.. browsers[row.Id]]))
                           .Where(recipient => recipient.Email is not null || recipient.Browsers.Count > 0)
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
