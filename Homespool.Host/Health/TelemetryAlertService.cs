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

using Homespool.Host.Localisation;
using Homespool.Host.Mail;

namespace Homespool.Host.Health;

/// <summary>
/// Emails administrators when the service becomes unhealthy, and again when it recovers.
/// </summary>
/// <remarks>
/// <para>
/// The last of three ways to notice the same thing: <c>/health</c> for a monitoring system, the
/// banner for whoever opens the app, and this for a box nobody is watching. Registered only when
/// SMTP is configured - without a mail server this would be a background service that exists to log
/// that it cannot do anything.
/// </para>
/// <para>
/// <b>Recipients are re-read every poll and kept when the read fails</b>, by
/// <see cref="AlertRecipients"/>: a closed administrator stops receiving alerts within a poll, and
/// an outage of the database the list lives in is still reported, to whoever was an administrator
/// at the last read that worked.
/// </para>
/// </remarks>
public sealed class TelemetryAlertService : BackgroundService
{
    /// <summary>
    /// How often health is sampled. Slow on purpose: this is a mail-sending path reacting to
    /// conditions measured in minutes, and the other two surfaces are already live.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly HealthCheckService _healthChecks;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryAlertService> _logger;

    private readonly AlertRecipients _recipients;

    private bool _alerted;

    public TelemetryAlertService(HealthCheckService healthChecks,
                                 IServiceScopeFactory scopeFactory,
                                 ILogger<TelemetryAlertService> logger)
    {
        _healthChecks = healthChecks;
        _scopeFactory = scopeFactory;
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
        IEnumerable<string> problems = report.Entries
                                             .Where(entry => entry.Value.Status != HealthStatus.Healthy)
                                             .Select(entry => $"<li>{entry.Value.Description ?? entry.Key}</li>");

        return $"<p>{localiser["Alert_UnhealthyIntro"].Value}</p><ul>{string.Concat(problems)}</ul>" +
               $"<p>{localiser["Alert_UnhealthyFooter"].Value}</p>";
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

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            HealthReport report = await _healthChecks.CheckHealthAsync(cancellationToken);

            // Whatever the health status: health does not imply a reachable database or its absence -
            // DiscardedEvents is cumulative, so a service stays Unhealthy long after the database
            // recovers. A read that fails keeps the last list rather than throwing, so the alert
            // below is still sent.
            await _recipients.RefreshAsync(cancellationToken);

            switch (AlertTransition.Decide(report.Status, _alerted))
            {
                case AlertAction.Alert:
                    _alerted = await SendAsync(
                        "Alert_UnhealthySubject",
                        localiser => Describe(report, localiser),
                        cancellationToken);
                    break;

                case AlertAction.Recovered:
                    await SendAsync(
                        "Alert_RecoveredSubject",
                        localiser => $"<p>{localiser["Alert_RecoveredBody"].Value}</p>",
                        cancellationToken);
                    _alerted = false;
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A failure to report a failure must not take the reporter down with it.
            _logger.LogError(e, "Health alert poll failed.");
        }
    }

    /// <summary>
    /// Returns whether anything was actually sent, so a send that failed does not mark the incident
    /// as reported - otherwise one unreachable mail server would suppress the alert permanently,
    /// and the recovery notice would be the first anyone heard of it.
    /// </summary>
    private async Task<bool> SendAsync(
        string subjectKey,
        Func<IStringLocalizer<SharedResource>, string> body,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertRecipient> recipients = _recipients.Current;

        if (recipients.Count == 0)
        {
            _logger.LogWarning("{Subject}, but no administrator address is known to send it to.", subjectKey);

            return false;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IEmailSender sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        IStringLocalizer<SharedResource> localiser =
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();

        bool anySent = false;

        foreach (AlertRecipient recipient in recipients)
        {
            // Composed per recipient rather than once for everyone: two administrators can read
            // Homespool in different languages, and there is no request here to inherit a culture
            // from. This is the path HSUser.Language exists for.
            (string subject, string message) = UserCultures.InCulture(
                recipient.Culture,
                () => (localiser[subjectKey].Value, body(localiser)));

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

        return anySent;
    }
}
