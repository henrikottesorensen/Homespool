using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Pages;
using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Notifications;

/// <summary>
/// Turns one happening into notifications: who may and wants to hear of it, and what each of them is
/// told, in their own language.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who hears is decided now, from the memberships as they are now</b> - not when the happening
/// was noticed, and not from anything stored with a destination. Somebody who left the team a minute
/// ago hears nothing; somebody deactivated hears nothing.
/// </para>
/// <para>
/// <b>The audience follows the permission to see the thing.</b> A printer waiting for a person is
/// news to everybody who may see the printer; a queue held, to everybody who may see the queue. A
/// print ending is its owner's news alone - the person who queued it - and a person who stopped their
/// own print is not told they did.
/// </para>
/// </remarks>
public sealed class NotificationRouter
{
    private readonly HomespoolDbContext _db;
    private readonly NotificationDestinationService _destinations;
    private readonly PrintHistoryService _history;
    private readonly ErrorText _errors;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<NotificationRouter> _logger;

    public NotificationRouter(HomespoolDbContext db,
                              NotificationDestinationService destinations,
                              PrintHistoryService history,
                              ErrorText errors,
                              IStringLocalizer<SharedResource> localiser,
                              ILogger<NotificationRouter> logger)
    {
        _db = db;
        _destinations = destinations;
        _history = history;
        _errors = errors;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="happening"/> to everybody who should hear of it, and answers how many
    /// people it was composed for.
    /// </summary>
    public async Task<int> SendAsync(PrinterHappening happening, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(happening);

        Printer? printer = await _db.Printers
                                    .AsNoTracking()
                                    .SingleOrDefaultAsync(row => row.Id == happening.PrinterId, cancellationToken);

        if (printer is null)
        {
            // Removed since - nothing left to point anybody at.
            return 0;
        }

        return happening switch
        {
            PrinterNeedsAttention waiting => await SendToTeamAsync(printer,
                                                                   Capability.ViewPrinter,
                                                                   NotificationKind.PrinterNeedsAttention,
                                                                   language => Task.FromResult<NotificationMessage?>(Attention(printer, waiting, language)),
                                                                   cancellationToken),

            FilamentChangeSoon soon => await SendToTeamAsync(printer,
                                                             Capability.ViewPrinter,
                                                             NotificationKind.FilamentChangeSoon,
                                                             language => Task.FromResult<NotificationMessage?>(FilamentSoon(printer, soon, language)),
                                                             cancellationToken),

            PrinterLost lost => await SendLostAsync(printer, lost, cancellationToken),

            QueueHeld => await SendToTeamAsync(printer,
                                               Capability.ViewQueue,
                                               NotificationKind.QueueHeld,
                                               null,
                                               cancellationToken),

            PrintEnded ended => await SendEndAsync(printer, ended, cancellationToken),

            _ => throw new ArgumentException($"No route for a {happening.GetType().Name}.", nameof(happening)),
        };
    }

    /// <summary>
    /// The tag every notification about one printer carries, so the newest replaces the rest on screen.
    /// </summary>
    public static string TagFor(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        return string.Create(CultureInfo.InvariantCulture, $"printer-{printer.Id}");
    }

    /// <summary>Where a notification about a printer leads.</summary>
    public static string UrlFor(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        return string.Create(CultureInfo.InvariantCulture, $"/Printers/Detail/{printer.Uuid}");
    }

    private async Task<int> SendToTeamAsync(Printer printer,
                                            Capability needed,
                                            NotificationKind kind,
                                            Func<string?, Task<NotificationMessage?>>? compose,
                                            CancellationToken cancellationToken)
    {
        IReadOnlyList<Recipient> recipients = await AudienceAsync(printer, needed, kind, cancellationToken);
        int told = 0;

        foreach (Recipient recipient in recipients)
        {
            // A queue hold is described by the page's own sentence, which is read per person because
            // it is behind the queue permission - and asked again here, so a hold cleared since it was
            // noticed says nothing.
            NotificationMessage? message = compose is not null ?
                await compose(recipient.Language) :
                await QueueHeldAsync(printer, recipient, cancellationToken);

            if (message is null)
            {
                continue;
            }

            await _destinations.DeliverToAllAsync(recipient.UserId, message, cancellationToken);
            told++;
        }

        return told;
    }

    private async Task<int> SendEndAsync(Printer printer, PrintEnded ended, CancellationToken cancellationToken)
    {
        PrintJob? job = await _db.PrintJobs
                                 .AsNoTracking()
                                 .SingleOrDefaultAsync(row => row.Id == ended.PrintJobId, cancellationToken);

        if (job is null)
        {
            return 0;
        }

        NotificationKind kind = job.State == PrintState.Finished ? NotificationKind.PrintFinished : NotificationKind.PrintDidNotFinish;

        // A person who stopped their own print knows.
        if (job.State == PrintState.Stopped && job.StoppedByUserId == job.QueuedByUserId)
        {
            return 0;
        }

        IReadOnlyList<Recipient> audience = await AudienceAsync(printer, Capability.ViewPrinter, kind, cancellationToken);
        Recipient? owner = audience.SingleOrDefault(recipient => recipient.UserId == job.QueuedByUserId);

        if (owner is null)
        {
            return 0;
        }

        NotificationMessage message = UserCultures.InCulture(owner.Language, () => new NotificationMessage(
            job.State switch
            {
                PrintState.Finished => _localiser["Notifications_FinishedTitle", PrinterDisplayName.For(printer)].Value,
                PrintState.Stopped => _localiser["Notifications_StoppedTitle", PrinterDisplayName.For(printer)].Value,
                _ => _localiser["Notifications_FailedTitle", PrinterDisplayName.For(printer)].Value,
            },
            job.FileName,
            UrlFor(printer),
            TagFor(printer),
            NotificationUrgency.Normal,
            TimeSpan.FromHours(12)));

        await _destinations.DeliverToAllAsync(owner.UserId, message, cancellationToken);

        return 1;
    }

    private async Task<int> SendLostAsync(Printer printer, PrinterLost lost, CancellationToken cancellationToken)
    {
        string? file = await _db.PrintJobs
                                .AsNoTracking()
                                .Where(job => job.Id == lost.PrintJobId)
                                .Select(job => job.FileName)
                                .SingleOrDefaultAsync(cancellationToken);

        return await SendToTeamAsync(printer,
                                     Capability.ViewPrinter,
                                     NotificationKind.PrinterLost,
                                     language => Task.FromResult<NotificationMessage?>(UserCultures.InCulture(language, () =>
                                         new NotificationMessage(
                                             _localiser["Notifications_LostTitle", PrinterDisplayName.For(printer)].Value,
                                             file is null ?
                                                 _localiser["Notifications_LostBodyNoFile"].Value :
                                                 _localiser["Notifications_LostBody", file].Value,
                                             UrlFor(printer),
                                             TagFor(printer),
                                             NotificationUrgency.High,
                                             TimeSpan.FromHours(1)))),
                                     cancellationToken);
    }

    private NotificationMessage FilamentSoon(Printer printer, FilamentChangeSoon soon, string? language)
    {
        // Rounded up: "about 5 minutes" at 4:10 left is the promise a person can walk over on.
        int minutes = Math.Max(1, (int)Math.Ceiling(soon.SecondsLeft / 60.0));

        return UserCultures.InCulture(language, () => new NotificationMessage(
            _localiser["Notifications_FilamentSoonTitle", PrinterDisplayName.For(printer)].Value,
            Plural.Format(_localiser, "Notifications_FilamentSoonBody", minutes),
            UrlFor(printer),
            TagFor(printer),
            NotificationUrgency.High,
            TimeSpan.FromMinutes(10)));
    }

    private NotificationMessage Attention(Printer printer, PrinterNeedsAttention waiting, string? language)
    {
        return UserCultures.InCulture(language, () =>
        {
            string title = waiting.Status == PrinterStatus.Error ?
                _localiser["Notifications_ErrorTitle", PrinterDisplayName.For(printer)].Value :
                _localiser["Notifications_AttentionTitle", PrinterDisplayName.For(printer)].Value;

            string body = AttentionRules.Reason(waiting.Status,
                                                waiting.Code,
                                                waiting.Text,
                                                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ??
                          _localiser["Notifications_AttentionNoReason"].Value;

            // High: this is the one kind that wants somebody to get up now.
            return new NotificationMessage(title, body, UrlFor(printer), TagFor(printer),
                                           NotificationUrgency.High, TimeSpan.FromHours(1));
        });
    }

    private async Task<NotificationMessage?> QueueHeldAsync(Printer printer, Recipient recipient, CancellationToken cancellationToken)
    {
        MessageKey? hold;

        try
        {
            hold = await _history.GetHoldReasonAsync(printer.Id, Caller.Unscoped(recipient.UserId), cancellationToken);
        }
        catch (Exceptions.TeamAccessDeniedException e)
        {
            // Refused by the queue permission after all - the membership changed between the audience
            // being read and this. Nothing to say to them.
            _logger.LogDebug(e, "[{PrinterId}] user {UserId} may no longer see the queue.", printer.Id, recipient.UserId);

            return null;
        }

        if (hold is null)
        {
            return null;
        }

        return UserCultures.InCulture(recipient.Language, () => new NotificationMessage(
            _localiser["Notifications_QueueHeldTitle", PrinterDisplayName.For(printer)].Value,
            _errors.For(hold),
            UrlFor(printer),
            TagFor(printer),
            NotificationUrgency.Normal,
            TimeSpan.FromHours(12)));
    }

    /// <summary>
    /// The active members of the printer's team whose membership allows <paramref name="needed"/>, who
    /// have not turned <paramref name="kind"/> off, and who have not muted this printer.
    /// </summary>
    private async Task<IReadOnlyList<Recipient>> AudienceAsync(Printer printer,
                                                               Capability needed,
                                                               NotificationKind kind,
                                                               CancellationToken cancellationToken)
    {
        int teamId = printer.TeamId;

        var members = await (from member in Memberships.Open(_db)
                             join account in _db.Users on member.UserId equals account.Id
                             where member.TeamId == teamId
                             select new
                             {
                                 member.UserId,
                                 member.Capabilities,
                                 account.Language,
                                 account.MutedNotifications,
                                 account.MutedPrinters,
                             })
                            .AsNoTracking()
                            .ToListAsync(cancellationToken);

        // Filtered here rather than in SQL: capabilities are a space-separated string, and matching
        // one by substring is the trap the capability set exists to avoid - as is matching an id in
        // the muted printers by substring.
        return [.. members.Where(member => CapabilitySet.Parse(member.Capabilities).Allows(needed))
                          .Where(member => !NotificationMutes.Parse(member.MutedNotifications).Contains(kind))
                          .Where(member => !NotificationMutes.ParsePrinters(member.MutedPrinters).Contains(printer.Uuid))
                          .Select(member => new Recipient(member.UserId, member.Language))];
    }

    /// <summary>Somebody to tell, and the language to tell them in.</summary>
    private sealed record Recipient(long UserId, string? Language);
}
