using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Mail;

/// <summary>
/// Connects to the configured SMTP server once at startup and reports the result. Diagnostic only.
/// </summary>
/// <remarks>
/// <para>
/// This <b>never changes behaviour</b>. Whether mail works is decided by configuration alone, so that the security
/// posture cannot shift with network weather - a mail server that happens to be down at boot must not silently
/// disable email confirmation.
/// </para>
/// <para>
/// It runs as a background service rather than inline in startup so a slow or unreachable server cannot delay the
/// app from serving requests. A failure here is a warning, not a fatal error: in a compose stack the mail container
/// may simply not be up yet, which is why <see cref="SmtpOptions.ProbeOnStartup"/> can turn it off.
/// </para>
/// </remarks>
public class SmtpConnectivityProbe : BackgroundService
{
    private readonly SmtpOptions _options;
    private readonly SmtpConnectivityCheck _check;
    private readonly ILogger<SmtpConnectivityProbe> _logger;

    public SmtpConnectivityProbe(IOptions<SmtpOptions> options,
                                 SmtpConnectivityCheck check,
                                 ILogger<SmtpConnectivityProbe> logger)
    {
        _options = options.Value;
        _check = check;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("No SMTP server is configured. Outgoing mail will be logged instead of sent, new " +
                               "accounts will be created already confirmed, and password reset will not work.");

            return;
        }

        if (!_options.ProbeOnStartup)
        {
            return;
        }

        SmtpCheckResult result = await _check.RunAsync(_options, stoppingToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _logger.LogInformation("SMTP check succeeded for {Host}:{Port}.", _options.Host, _options.Port);

            return;
        }

        _logger.LogWarning("SMTP check failed for {Host}:{Port}: {Error}. Mail will be attempted anyway; this is a " +
                           "diagnostic only. If the mail server starts after this one, set Smtp:ProbeOnStartup " +
                           "to false.",
                           _options.Host,
                           _options.Port,
                           result.Error);
    }
}
