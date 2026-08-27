using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Services;
using Homespool.Model.Entities;

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
/// <b>Recipients are cached deliberately.</b> They live in the database, and the alert worth sending
/// most is the one about the database being unreachable - resolving them on demand would fail
/// exactly when it matters. The list is refreshed on every healthy poll and used from cache when
/// things go wrong, so an outage is reported to whoever was an administrator when things last
/// worked.
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

    private IReadOnlyList<string> _recipients = [];
    private bool _alerted;

    public TelemetryAlertService(HealthCheckService healthChecks,
                                 IServiceScopeFactory scopeFactory,
                                 ILogger<TelemetryAlertService> logger)
    {
        _healthChecks = healthChecks;
        _scopeFactory = scopeFactory;
        _logger = logger;
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

        return $"<p>{localiser["Alert_UnhealthyIntro"].Value}</p><ul>{string.Concat(problems)}</ul>"
               + $"<p>{localiser["Alert_UnhealthyFooter"].Value}</p>";
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

            if (ShouldRefreshRecipients())
            {
                // Attempted whatever the health status. This used to run only while Healthy, on the
                // reasoning that health implies a reachable database - but it does not: DiscardedEvents
                // is cumulative, so a service stays Unhealthy long after the database recovers. Gating
                // on it meant a writer that had ever dropped events could never learn who to tell.
                // Having nobody to alert is exactly when the attempt is worth making; if the database
                // really is down this throws, is caught below, and is retried on the next poll.
                await RefreshRecipientsAsync(cancellationToken);
            }

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
        if (_recipients.Count == 0)
        {
            _logger.LogWarning("{Subject}, but no administrator address is known to send it to.", subjectKey);

            return false;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IEmailSender sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        IStringLocalizer<SharedResource> localiser =
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
        UserCultures cultures = scope.ServiceProvider.GetRequiredService<UserCultures>();

        bool anySent = false;

        foreach (string recipient in _recipients)
        {
            // Composed per recipient rather than once for everyone: two administrators can read
            // Homespool in different languages, and there is no request here to inherit a culture
            // from. This is the path HSUser.Language exists for.
            string? culture = await cultures.ForEmailAsync(recipient, cancellationToken);

            (string subject, string message) = UserCultures.InCulture(
                culture,
                () => (localiser[subjectKey].Value, body(localiser)));

            EmailSendResult result = await sender.SendEmailAsync(recipient, subject, message);

            if (result == EmailSendResult.Sent)
            {
                anySent = true;
            }
            else
            {
                _logger.LogWarning("Could not email the health alert to {Recipient}.", recipient);
            }
        }

        return anySent;
    }

    /// <summary>
    /// Read once, and only until there is somebody to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading a single time at startup would be enough if administrators existed by then, but a
    /// fresh deployment starts before anyone has been through <c>/setup</c>, so the first read finds
    /// nobody. Retrying only while the list is empty covers that without polling for something that
    /// never changes: an ordinary start with an administrator already present reads once and stops,
    /// and a new install picks the first one up within a poll of setup completing.
    /// </para>
    /// <para>
    /// The list barely moves: <c>AddToRoleAsync</c> for the administrator role is called in exactly
    /// one place, <c>Setup.OnPostAsync</c>, and nothing ever revokes it - so the population is fixed
    /// at setup. The single way this cache can go stale is an administrator changing their own
    /// address on Account/Manage/Email, after which alerts go to the old one until the service
    /// restarts. Accepted deliberately: closing it means either a hook from that page into this
    /// service, or re-reading on a schedule for a list that otherwise never changes at all.
    /// </para>
    /// </remarks>
    private bool ShouldRefreshRecipients()
    {
        return _recipients.Count == 0;
    }

    private async Task RefreshRecipientsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        IList<HSUser> admins = await users.GetUsersInRoleAsync(AdminBootstrap.AdminRole);

        _recipients = admins.Select(a => a.Email)
                            .Where(email => !string.IsNullOrWhiteSpace(email))
                            .Select(email => email!)
                            .ToList();
    }
}
