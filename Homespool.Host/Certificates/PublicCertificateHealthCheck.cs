using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Certificates;

/// <summary>
/// Reports when a name in <c>ACME_HOSTS</c> is served a certificate that is close to expiry, expired,
/// or not one a browser trusts - read from the proxy, the way a browser reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asking the proxy, rather than reading files or a report the host writes</b>, because every way
/// the renewal fails is quiet and most of them leave the files looking fine: a renewal timer never
/// enabled, a certificate written where the proxy does not look, one renewed but never loaded because
/// the proxy was not restarted, a name still on the self-signed certificate it started with. What the
/// proxy serves is the one thing all of those change, and the only thing a browser sees.
/// </para>
/// <para>
/// <b>Unhealthy from <see cref="PublicCertificateOptions.AlertDays"/> out, Degraded before that</b>,
/// because Unhealthy is what sends the alert mail and push, and Degraded is the banner alone. A
/// certificate a browser refuses is Unhealthy at once: that is the site already failing, under that
/// name. A proxy that cannot be reached is only Degraded - the site is down for a reason this check
/// is not about, and saying so belongs to whatever watches the proxy.
/// </para>
/// <para>
/// <b>Asked at most once an hour</b>, where the alert service polls every minute: a certificate's
/// expiry moves by the day, and a probe per poll would be a handshake a minute in the proxy's log.
/// An unreachable proxy is asked again after a minute instead, so a proxy that starts after the
/// application - which compose arranges - does not leave an hour's banner behind it. Registered as a
/// singleton for this, as <c>UpdateReportHealthCheck</c> is.
/// </para>
/// <para>
/// Never tagged for liveness: a restart serves the same certificate.
/// </para>
/// </remarks>
public sealed class PublicCertificateHealthCheck : IHealthCheck
{
    private const string RenewalAdvice =
        "On the host, journalctl -u homespool-renew-cert.service -n 50 says what the renewal last did, " +
        "and systemctl status homespool-renew-cert.timer whether it still runs.";

    /// <summary>How long a judgement stands.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(1);

    /// <summary>How long a judgement stands when the proxy could not be reached.</summary>
    public static readonly TimeSpan RecheckUnreachableAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long one name's handshake may take.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IOptions<PublicCertificateOptions> _options;
    private readonly IPublicCertificateProbe _probe;
    private readonly TimeProvider _time;

    private readonly Lock _gate = new();

    private HealthCheckResult? _last;
    private DateTimeOffset _lastValidUntil;
    private Task<(HealthCheckResult result, TimeSpan validFor)>? _inFlight;

    public PublicCertificateHealthCheck(IOptions<PublicCertificateOptions> options,
                                        IPublicCertificateProbe probe,
                                        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _probe = probe;
        _time = time;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        PublicCertificateOptions options = _options.Value;
        (IReadOnlyList<string> names, IReadOnlyList<string> unusable) = ParseHosts(options.Hosts);

        if (names.Count == 0 && unusable.Count == 0)
        {
            return HealthCheckResult.Healthy(
                "No name is set to be served a publicly-trusted certificate (ACME_HOSTS is empty), so the " +
                "proxy serves its own self-signed certificates and nothing here is close to expiry.");
        }

        if (string.IsNullOrWhiteSpace(options.ProxyHost))
        {
            return HealthCheckResult.Healthy(
                "ACME_HOSTS names a certificate to check, but no proxy is configured to ask for it.");
        }

        Task<(HealthCheckResult result, TimeSpan validFor)> pending;

        lock (_gate)
        {
            if (_last is { } last && _time.GetUtcNow() < _lastValidUntil)
            {
                return last;
            }

            // One probe however many ask: /health and the alert poll can land together, and each
            // would otherwise open its own handshakes.
            pending = _inFlight ??= ProbeAndJudgeAsync(options, names, unusable);
        }

        // The probe is shared, so it runs on its own timeouts; a caller giving up stops waiting for it
        // and leaves it to finish for the next.
        (HealthCheckResult result, TimeSpan validFor) = await pending.WaitAsync(cancellationToken);

        lock (_gate)
        {
            if (ReferenceEquals(_inFlight, pending))
            {
                _last = result;
                _lastValidUntil = _time.GetUtcNow() + validFor;
                _inFlight = null;
            }
        }

        return result;
    }

    /// <summary>
    /// The names in an <c>ACME_HOSTS</c> value, and the entries no certificate could be issued for.
    /// </summary>
    /// <remarks>
    /// The test the renewal script applies, so the two agree on which entries are names: whitespace
    /// anywhere in an entry is a typo and is dropped, and a name is letters, digits, dots and hyphens,
    /// not starting with a dot.
    /// </remarks>
    public static (IReadOnlyList<string> names, IReadOnlyList<string> unusable) ParseHosts(string? hosts)
    {
        List<string> names = [];
        List<string> unusable = [];

        foreach (string raw in (hosts ?? string.Empty).Split(';'))
        {
            string entry = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));

            if (entry.Length == 0)
            {
                continue;
            }

            bool usable = entry[0] != '.' && entry.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
            List<string> into = usable ? names : unusable;

            if (!into.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                into.Add(entry);
            }
        }

        return (names, unusable);
    }

    private async Task<(HealthCheckResult result, TimeSpan validFor)> ProbeAndJudgeAsync(
        PublicCertificateOptions options, IReadOnlyList<string> names, IReadOnlyList<string> unusable)
    {
        // Off the caller's thread before the first await, so the lock above is never held across a
        // handshake that happens to complete synchronously.
        await Task.Yield();

        List<(string name, PublicCertificateProbeResult probe)> probes = [];

        foreach (string name in names)
        {
            probes.Add((name, await ProbeOneAsync(options, name)));
        }

        HealthCheckResult result = Judge(probes, unusable, options, _time.GetUtcNow());
        bool unreachable = probes.Any(p => p.probe.Outcome == PublicCertificateProbeOutcome.Unreachable);

        return (result, unreachable ? RecheckUnreachableAfter : RecheckAfter);
    }

    private async Task<PublicCertificateProbeResult> ProbeOneAsync(PublicCertificateOptions options, string name)
    {
        using CancellationTokenSource timeout = new(ProbeTimeout, _time);

        try
        {
            return await _probe.ProbeAsync(options.ProxyHost, options.ProxyPort, name, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return PublicCertificateProbeResult.Unreachable(
                $"no answer within {ProbeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds");
        }
        catch (Exception ex)
        {
            // Anything else the probe throws is still a name that was not checked. Left to escape it
            // would fault the shared task, and _inFlight - cleared only after a probe completes - would
            // hand that same failure to every later caller until the application restarted.
            return PublicCertificateProbeResult.Unreachable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>What the probes found, as one health result.</summary>
    /// <param name="probes">What the proxy served for each name.</param>
    /// <param name="unusable">Entries of <c>ACME_HOSTS</c> that are not names.</param>
    /// <param name="options">The thresholds and the proxy's address.</param>
    /// <param name="now">The time to judge expiry against.</param>
    /// <returns>The worst of the names' states, with a sentence for each name that is wrong.</returns>
    public static HealthCheckResult Judge(IReadOnlyList<(string name, PublicCertificateProbeResult probe)> probes,
                                          IReadOnlyList<string> unusable,
                                          PublicCertificateOptions options,
                                          DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(unusable);
        ArgumentNullException.ThrowIfNull(options);

        HealthStatus worst = HealthStatus.Healthy;
        List<string> problems = [];
        List<string> fine = [];
        Dictionary<string, object> data = new(StringComparer.Ordinal);

        foreach (string entry in unusable)
        {
            worst = HealthStatus.Unhealthy;
            problems.Add($"\"{entry}\" in ACME_HOSTS is not a name a certificate can be issued for, so the renewal refuses it.");
            data[entry] = "unusable";
        }

        List<string> unreachable = [];

        // Whether anything wrong is the renewal's to fix, which decides whether to say where its log is.
        bool aboutRenewal = unusable.Count > 0;

        foreach ((string name, PublicCertificateProbeResult probe) in probes)
        {
            (HealthStatus status, string? sentence) = JudgeOne(name, probe, options, now);

            if (probe.Outcome == PublicCertificateProbeOutcome.Unreachable)
            {
                unreachable.Add(name);
                data[name] = $"unreachable: {probe.Detail}";
            }
            else
            {
                data[name] = probe.NotAfter is { } notAfter ?
                    $"{probe.Outcome}, valid until {notAfter:yyyy-MM-dd HH:mm} UTC, {probe.Errors}" :
                    $"{probe.Outcome}: {probe.Detail}";
            }

            if (status < worst)
            {
                worst = status;
            }

            if (status == HealthStatus.Healthy)
            {
                fine.Add($"{name} until {probe.NotAfter:yyyy-MM-dd}");
            }
            else if (sentence is not null)
            {
                problems.Add(sentence);
                aboutRenewal = true;
            }
        }

        if (unreachable.Count > 0)
        {
            // One sentence for all of them: the proxy is one listener, and a list of names each
            // "could not be reached" says the same thing several times.
            string detail = probes.First(p => p.probe.Outcome == PublicCertificateProbeOutcome.Unreachable).probe.Detail ?? "no answer";
            problems.Add($"The proxy at {options.ProxyHost}:{options.ProxyPort.ToString(CultureInfo.InvariantCulture)} " +
                         $"could not be reached ({detail}), so the certificate served for {JoinNames(unreachable)} " +
                         "was not checked.");
        }

        if (worst == HealthStatus.Healthy)
        {
            return HealthCheckResult.Healthy(
                $"The publicly-trusted {(fine.Count == 1 ? "certificate is" : "certificates are")} " +
                $"valid: {string.Join(", ", fine)}.",
                data);
        }

        string description = string.Join(" ", problems) + (aboutRenewal ? " " + RenewalAdvice : string.Empty);

        return new HealthCheckResult(worst, description, data: data);
    }

    private static (HealthStatus status, string? sentence) JudgeOne(string name,
                                                                    PublicCertificateProbeResult probe,
                                                                    PublicCertificateOptions options,
                                                                    DateTimeOffset now)
    {
        switch (probe.Outcome)
        {
            case PublicCertificateProbeOutcome.Unreachable:
                return (HealthStatus.Degraded, null);

            case PublicCertificateProbeOutcome.Refused:
                return (HealthStatus.Unhealthy,
                        $"The proxy would not complete a TLS handshake for {name} ({probe.Detail}), so browsers " +
                        "cannot reach the site under that name.");
        }

        if (probe.Outcome != PublicCertificateProbeOutcome.Served || probe.NotAfter is not { } notAfter)
        {
            throw new InvalidOperationException($"A probe of {name} came back {probe.Outcome} with no certificate.");
        }

        // Expiry before trust: an expired certificate fails validation too, and "expired" is the
        // sentence that says what to do about it.
        if (notAfter <= now)
        {
            return (HealthStatus.Unhealthy,
                    $"The certificate served for {name} expired on {notAfter:yyyy-MM-dd}, and browsers refuse " +
                    "the site under that name.");
        }

        if ((probe.Errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return (HealthStatus.Unhealthy,
                    $"The certificate served for {name} does not cover that name, and browsers refuse it. The " +
                    "proxy answers a name it has no server for with another name's certificate, so check that " +
                    "the name is in USER_HOSTS as well as ACME_HOSTS.");
        }

        if (probe.Errors != SslPolicyErrors.None)
        {
            return (HealthStatus.Unhealthy,
                    $"The certificate served for {name} is not one browsers trust - most likely the self-signed " +
                    "one the proxy falls back to when it finds no issued certificate for the name. Either none " +
                    "has been issued yet, or the proxy has not been restarted since one was.");
        }

        int days = (int)Math.Floor((notAfter - now).TotalDays);
        string when = days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";

        if (days <= options.AlertDays)
        {
            return (HealthStatus.Unhealthy,
                    $"The certificate served for {name} expires in {when}, on {notAfter:yyyy-MM-dd}, and has not " +
                    "been renewed; renewal should have replaced it weeks ago.");
        }

        if (days <= options.WarnDays)
        {
            return (HealthStatus.Degraded,
                    $"The certificate served for {name} expires in {when}, on {notAfter:yyyy-MM-dd}; renewal " +
                    "should already have replaced it.");
        }

        return (HealthStatus.Healthy, null);
    }

    /// <summary>Names as a sentence lists them: "a", "a and b", "a, b and c".</summary>
    private static string JoinNames(IReadOnlyList<string> names)
    {
        return names.Count == 1 ?
            names[0] :
            $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }
}
