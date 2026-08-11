using System;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Security;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Services;

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
    private readonly ISmtpTransportFactory _transportFactory;
    private readonly ILogger<SmtpConnectivityProbe> _logger;

    public SmtpConnectivityProbe(IOptions<SmtpOptions> options,
                                 ISmtpTransportFactory transportFactory,
                                 ILogger<SmtpConnectivityProbe> logger)
    {
        _options = options.Value;
        _transportFactory = transportFactory;
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

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using ISmtpTransport client = _transportFactory.Create();

            SecureSocketOptions socketOptions = _options.DisableTls ? SecureSocketOptions.None
                : _options.UseImplicitTls ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, timeout.Token);

            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                // Authenticating is the point of the probe: a reachable port proves very little, and bad credentials
                // are the failure an operator is most likely to have and least likely to notice until a user needs
                // a password reset.
                await client.AuthenticateAsync(_options.UserName, _options.Password, timeout.Token);
            }
            else
            {
                _logger.LogInformation("SMTP has no username configured; connecting without authentication.");
            }

            await client.DisconnectAsync(true, timeout.Token);

            _logger.LogInformation("SMTP check succeeded for {Host}:{Port}.", _options.Host, _options.Port);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-probe. Not interesting.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                               "SMTP check failed for {Host}:{Port}. Mail will be attempted anyway; this is a " +
                               "diagnostic only. If the mail server starts after this one, set Smtp:ProbeOnStartup " +
                               "to false.",
                               _options.Host,
                               _options.Port);
        }
    }
}
