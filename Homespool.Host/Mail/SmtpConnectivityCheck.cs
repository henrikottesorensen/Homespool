using System;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Security;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Mail;

/// <summary>
/// Connects to a mail server, authenticates if asked to, and disconnects - reporting what happened
/// rather than sending anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>One sequence, two callers.</b> The startup probe and the settings page's test button ask the
/// same question and must not be able to answer it differently - a button that connects in a way
/// startup does not would report a working server the deployment then fails to use.
/// </para>
/// <para>
/// <b>It takes the options rather than reading them</b>, which is what lets the page check values
/// that are not in force yet. Mail settings only take effect at the next restart, so a button reading
/// the running configuration would test what the deployment is doing rather than what somebody just
/// typed - the one thing they are asking about.
/// </para>
/// <para>
/// <b>Authenticating is the point.</b> A reachable port proves very little; bad credentials are the
/// failure an operator is most likely to have and least likely to notice until somebody needs a
/// password reset.
/// </para>
/// </remarks>
public sealed class SmtpConnectivityCheck
{
    private readonly ISmtpTransportFactory _transportFactory;
    private readonly ILogger<SmtpConnectivityCheck> _logger;

    /// <summary>Creates the check.</summary>
    /// <param name="transportFactory">Makes the client, so a test can substitute one.</param>
    /// <param name="logger">Where an unauthenticated connection is noted.</param>
    public SmtpConnectivityCheck(ISmtpTransportFactory transportFactory, ILogger<SmtpConnectivityCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);

        _transportFactory = transportFactory;
        _logger = logger;
    }

    /// <summary>
    /// Tries the server the options describe.
    /// </summary>
    /// <param name="options">The settings to try, in force or not.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>What happened.</returns>
    public async Task<SmtpCheckResult> RunAsync(SmtpOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return new SmtpCheckResult(false, "No mail server is configured.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(options.Timeout);

        try
        {
            using ISmtpTransport client = _transportFactory.Create();

            SecureSocketOptions socketOptions = options.DisableTls ? SecureSocketOptions.None
                : options.UseImplicitTls ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(options.Host, options.Port, socketOptions, timeout.Token)
                        .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password, timeout.Token)
                            .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("SMTP check has no username configured; connecting without authentication.");
            }

            await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);

            return new SmtpCheckResult(true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SmtpCheckResult(false, "Timed out.");
        }
#pragma warning disable CA1031 // The caller wants an answer, not an exception: any failure to reach a
        // mail server is a result to report, and MailKit throws several unrelated types for it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new SmtpCheckResult(false, exception.Message);
        }
    }
}

/// <summary>What a connection attempt did.</summary>
/// <param name="Succeeded">Whether the server was reached and, where asked, authenticated.</param>
/// <param name="Error">
/// The server's or client's own words when it did not. Machine text, shown as it came rather than
/// translated - it is the detail somebody pastes into a search.
/// </param>
public sealed record SmtpCheckResult(bool Succeeded, string? Error);
