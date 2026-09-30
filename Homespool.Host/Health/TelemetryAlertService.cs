using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Health;

/// <summary>
/// Tells administrators when the service becomes unhealthy, and again when it recovers - by email when
/// a mail server is configured, and in every browser they have enabled notifications in.
/// </summary>
/// <remarks>
/// <para>
/// The last of three ways to notice the same thing: <c>/health</c> for a monitoring system, the
/// banner for whoever opens the app, and this for a box nobody is watching. Registered whether or not
/// mail is: a deployment without a mail server can still reach an administrator's phone.
/// </para>
/// <para>
/// <b>Recipients are re-read every poll and kept when the read fails</b>, by
/// <see cref="AlertRecipients"/>, browsers included: a closed administrator stops receiving alerts
/// within a poll, and an outage of the database the list lives in is still reported, to whoever was
/// an administrator at the last read that worked.
/// </para>
/// <para>
/// <b>Not through the notification queue.</b> Everything there is about a printer and is routed by
/// reading the database when it is sent, which is the one thing an alert about the database cannot
/// rely on. <see cref="AlertTransition"/> already sends each incident once, so the throttle the queue
/// has would have nothing to do. A browser is delivered to directly and the outcome recorded on its
/// row afterwards, if the database will take it.
/// </para>
/// </remarks>
public sealed class TelemetryAlertService : BackgroundService
{
    /// <summary>
    /// The tag every health notification carries, so the all-clear replaces the alert on screen.
    /// </summary>
    public const string HealthTag = "homespool-health";

    /// <summary>
    /// The most of the list of problems a push carries. A notification shows a few lines of it, the
    /// page it opens shows the banner, and the email has all of it; a push service takes 4 KB at most,
    /// and a character outside ASCII costs six bytes of that once the payload's JSON has escaped it.
    /// </summary>
    public const int MaxPushBodyLength = 500;

    /// <summary>
    /// Where a health notification leads: the front page, which carries the banner an administrator
    /// is shown on every page.
    /// </summary>
    public const string PushUrl = "/";

    /// <summary>
    /// How often health is sampled. Slow on purpose: this is a mail-sending path reacting to
    /// conditions measured in minutes, and the other two surfaces are already live.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long a health notification is worth delivering to a device that is off.</summary>
    private static readonly TimeSpan PushTimeToLive = TimeSpan.FromHours(12);

    private readonly HealthCheckService _healthChecks;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _mailConfigured;
    private readonly ILogger<TelemetryAlertService> _logger;

    private readonly AlertRecipients _recipients;

    /// <summary>The checks of the incident already reported; empty when there is none.</summary>
    private IReadOnlySet<string> _incident = new HashSet<string>();

    /// <summary>Whether the push key has been read; asked for every poll until it has.</summary>
    private bool _pushKeyLoaded;

    /// <summary>
    /// Whether "nobody to send to" has been logged since there was last nothing to report, so an
    /// unhealthy deployment with nobody to tell says so once rather than every poll.
    /// </summary>
    private bool _saidNobody;

    public TelemetryAlertService(HealthCheckService healthChecks,
                                 IServiceScopeFactory scopeFactory,
                                 IOptions<SmtpOptions> smtp,
                                 ILogger<TelemetryAlertService> logger)
    {
        ArgumentNullException.ThrowIfNull(smtp);

        _healthChecks = healthChecks;
        _scopeFactory = scopeFactory;
        _mailConfigured = smtp.Value.IsConfigured;
        _logger = logger;
        _recipients = new AlertRecipients(scopeFactory, logger);
    }

    /// <summary>Reuses each check's own description, so the email, the banner and <c>/health</c> all
    /// say the same thing about the same condition.</summary>
    /// <remarks>
    /// <b>The prose around the list is localised; the list itself is not.</b> Each item is a health
    /// check's own description, or failing that its key - text this application does not author and
    /// which names a component rather than describing it to a reader. Translating those would put
    /// three surfaces out of step with each other for no gain, since the banner and <c>/health</c>
    /// carry the same strings untranslated.
    /// </remarks>
    private static string Describe(HealthReport report, IStringLocalizer<SharedResource> localiser)
    {
        IEnumerable<string> problems = Problems(report).Select(problem => $"<li>{problem}</li>");

        return $"<p>{localiser["Alert_UnhealthyIntro"].Value}</p><ul>{string.Concat(problems)}</ul>" +
               $"<p>{localiser["Alert_UnhealthyFooter"].Value}</p>";
    }

    /// <summary>
    /// The same list as <see cref="Describe"/>, as plain text for a notification - one problem a line,
    /// cut at <see cref="MaxPushBodyLength"/>.
    /// </summary>
    public static string DescribeForPush(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        string text = string.Join('\n', Problems(report));

        if (text.Length <= MaxPushBodyLength)
        {
            return text;
        }

        int cut = MaxPushBodyLength - 1;

        // Never between the two halves of a character outside the Basic Multilingual Plane.
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }

    private static IEnumerable<string> Problems(HealthReport report)
    {
        return report.Entries
                     .Where(entry => entry.Value.Status != HealthStatus.Healthy)
                     .Select(entry => entry.Value.Description ?? entry.Key);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval);

        try
        {
            // Once before the first tick, so a service that starts up already broken says so rather
            // than waiting out a poll interval first.
            await PollAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PollAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One poll. Public so a test can drive it without waiting for the timer.</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Before anything can go wrong: the push key lives in the database, and an alert about the
            // database is the one that could not read it.
            await LoadPushKeyAsync(cancellationToken);

            HealthReport report = await _healthChecks.CheckHealthAsync(cancellationToken);

            // Whatever the health status: health does not imply a reachable database or its absence -
            // DiscardedEvents is cumulative, so a service stays Unhealthy long after the database
            // recovers. A read that fails keeps the last list rather than throwing, so the alert
            // below is still sent.
            await _recipients.RefreshAsync(cancellationToken);

            AlertDecision decision = AlertTransition.Decide(
                report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status),
                _incident);

            switch (decision.Action)
            {
                case AlertAction.Alert:
                    // Only once it has gone out: an incident nobody was told about is not reported.
                    if (await SendAsync(
                            "Alert_UnhealthySubject",
                            localiser => Describe(report, localiser),
                            _ => DescribeForPush(report),
                            NotificationUrgency.High,
                            cancellationToken))
                    {
                        _incident = decision.Incident;
                    }

                    break;

                case AlertAction.Recovered:
                    await SendAsync(
                        "Alert_RecoveredSubject",
                        localiser => $"<p>{localiser["Alert_RecoveredBody"].Value}</p>",
                        localiser => localiser["Alert_RecoveredBody"].Value,
                        NotificationUrgency.Normal,
                        cancellationToken);
                    _incident = decision.Incident;
                    break;

                default:
                    // An open incident grows by any check that has turned Unhealthy since.
                    _incident = decision.Incident;
                    break;
            }

            // Nothing wrong now, so the next thing that goes wrong is news to the log as well.
            if (decision.Incident.Count == 0)
            {
                _saidNobody = false;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A failure to report a failure must not take the reporter down with it.
            _logger.LogError(e, "Health alert poll failed.");
        }
    }

    private async Task LoadPushKeyAsync(CancellationToken cancellationToken)
    {
        if (_pushKeyLoaded)
        {
            return;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        // Absent only from a host assembled without notifications.
        VapidKeyStore? keys = scope.ServiceProvider.GetService<VapidKeyStore>();

        if (keys is null)
        {
            return;
        }

        try
        {
            await keys.GetAsync(cancellationToken);
            _pushKeyLoaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the push key; health alerts reach nobody's browser until it can be read.");
        }
    }

    /// <summary>
    /// Returns whether anything was actually sent, so a send that failed does not mark the incident
    /// as reported - otherwise one unreachable mail server would suppress the alert permanently,
    /// and the recovery notice would be the first anyone heard of it.
    /// </summary>
    private async Task<bool> SendAsync(
        string subjectKey,
        Func<IStringLocalizer<SharedResource>, string> emailBody,
        Func<IStringLocalizer<SharedResource>, string> pushBody,
        NotificationUrgency urgency,
        CancellationToken cancellationToken)
    {
        List<AlertRecipient> recipients = [.. _recipients.Current.Where(recipient => (_mailConfigured && recipient.Email is not null) ||
                                                                                     recipient.Browsers.Count > 0)];

        if (recipients.Count == 0)
        {
            if (!_saidNobody)
            {
                _logger.LogWarning("{Subject}, but no administrator has an address or a browser to send it to.", subjectKey);
                _saidNobody = true;
            }

            return false;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IEmailSender sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        IStringLocalizer<SharedResource> localiser =
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
        WebPushChannel? push = scope.ServiceProvider.GetService<WebPushChannel>();

        bool anySent = false;

        foreach (AlertRecipient recipient in recipients)
        {
            // Composed per recipient rather than once for everyone: two administrators can read
            // Homespool in different languages, and there is no request here to inherit a culture
            // from. This is the path HSUser.Language exists for.
            (string subject, string message, string notice) = UserCultures.InCulture(
                recipient.Culture,
                () => (localiser[subjectKey].Value, emailBody(localiser), pushBody(localiser)));

            if (_mailConfigured && recipient.Email is not null)
            {
                EmailSendResult result = await sender.SendEmailAsync(recipient.Email, subject, message);

                if (result == EmailSendResult.Sent)
                {
                    anySent = true;
                }
                else
                {
                    _logger.LogWarning("Could not email the health alert to {Recipient}.", recipient.Email);
                }
            }

            if (push is null)
            {
                continue;
            }

            NotificationMessage notification = new(subject, notice, PushUrl, HealthTag, urgency, PushTimeToLive);

            foreach (WebPushDestination browser in recipient.Browsers)
            {
                if (await PushAsync(push, browser, notification, cancellationToken))
                {
                    anySent = true;
                }
            }
        }

        if (anySent)
        {
            _saidNobody = false;
        }

        return anySent;
    }

    /// <summary>
    /// Delivers to one browser and records how it went on the browser's row, if the database will take
    /// it - an outage is what this may be reporting, and must not stop the next browser being tried.
    /// </summary>
    private async Task<bool> PushAsync(WebPushChannel push,
                                       WebPushDestination browser,
                                       NotificationMessage notification,
                                       CancellationToken cancellationToken)
    {
        DeliveryOutcome outcome;

        try
        {
            outcome = await push.DeliverAsync(browser, notification, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not push the health alert to browser subscription {DestinationId}.", browser.Uuid);

            return false;
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<NotificationDestinationService>()
                       .RecordAsync(browser.Uuid, outcome, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not record how the health alert to browser subscription {DestinationId} went ({Outcome}).",
                               browser.Uuid, outcome);
        }

        return outcome == DeliveryOutcome.Delivered;
    }
}
