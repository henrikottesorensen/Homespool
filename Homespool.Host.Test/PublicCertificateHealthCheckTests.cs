using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Certificates;
using Homespool.Host.Health;

namespace Homespool.Host.Test;

/// <summary>
/// Whether a name in <c>ACME_HOSTS</c> is served a certificate a browser accepts, and for how much
/// longer - judged from what the proxy serves, so every quiet way the renewal fails ends up here.
/// </summary>
/// <remarks>
/// The status decides who is told: Unhealthy mails and pushes the administrators, Degraded is the
/// banner alone. So each case asserts the status, and the description, which the banner shows
/// verbatim.
/// </remarks>
public sealed class PublicCertificateHealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers from a table, and counts what it was asked.</summary>
    private sealed class TableProbe : IPublicCertificateProbe
    {
        private readonly Dictionary<string, PublicCertificateProbeResult> _answers;

        public TableProbe(Dictionary<string, PublicCertificateProbeResult> answers)
        {
            _answers = answers;
        }

        public List<string> Asked { get; } = [];

        public Task<PublicCertificateProbeResult> ProbeAsync(string host, int port, string name,
                                                             CancellationToken cancellationToken)
        {
            Asked.Add(name);
            return Task.FromResult(_answers[name]);
        }
    }

    private static PublicCertificateProbeResult Expiring(int days, SslPolicyErrors errors = SslPolicyErrors.None)
    {
        return PublicCertificateProbeResult.Served(Now.AddDays(days).AddHours(1), errors);
    }

    private static (PublicCertificateHealthCheck check, TableProbe probe, FakeTimeProvider time) NewCheck(
        string hosts, Dictionary<string, PublicCertificateProbeResult> answers, string proxyHost = "proxy")
    {
        TableProbe probe = new(answers);
        FakeTimeProvider time = new(Now);
        PublicCertificateHealthCheck check = new(
            Options.Create(new PublicCertificateOptions { Hosts = hosts, ProxyHost = proxyHost }), probe, time);

        return (check, probe, time);
    }

    private static Task<HealthCheckResult> RunAsync(PublicCertificateHealthCheck check)
    {
        return check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    [Fact]
    public async Task No_ACME_HOSTS_is_healthy_and_asks_nothing()
    {
        (PublicCertificateHealthCheck check, TableProbe probe, _) = NewCheck(string.Empty, []);

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("ACME_HOSTS is empty");
        probe.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task No_proxy_to_ask_is_healthy_and_asks_nothing()
    {
        (PublicCertificateHealthCheck check, TableProbe probe, _) = NewCheck("a.example.com", [], proxyHost: string.Empty);

        (await RunAsync(check)).Status.Should().Be(HealthStatus.Healthy);
        probe.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trusted_certificate_well_inside_its_life_is_healthy_and_says_until_when()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck("a.example.com", new() { ["a.example.com"] = Expiring(60) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("a.example.com until 2026-12-01");
    }

    [Fact]
    public async Task Inside_the_warning_window_is_degraded_so_the_banner_says_it_and_nobody_is_mailed()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck("a.example.com", new() { ["a.example.com"] = Expiring(20) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("a.example.com expires in 20 days");
        result.Description.Should().Contain("journalctl -u homespool-renew-cert.service");
    }

    [Fact]
    public async Task A_week_from_expiry_is_unhealthy_so_the_administrators_are_mailed()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck("a.example.com", new() { ["a.example.com"] = Expiring(7) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("expires in 7 days");
    }

    [Fact]
    public async Task One_day_left_says_one_day()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck("a.example.com", new() { ["a.example.com"] = Expiring(1) });

        (await RunAsync(check)).Description.Should().Contain("expires in 1 day,");
    }

    [Fact]
    public async Task An_expired_certificate_says_expired_rather_than_untrusted()
    {
        // Validation fails an expired certificate too; "expired" is the sentence that says what to do.
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "a.example.com",
            new() { ["a.example.com"] = Expiring(-3, SslPolicyErrors.RemoteCertificateChainErrors) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("expired on 2026-09-29");
    }

    [Fact]
    public async Task The_self_signed_fallback_is_unhealthy_however_long_it_has_left()
    {
        // The proxy's own certificate lasts ten years, so its expiry says nothing: a name meant to be
        // publicly trusted that is served it is a site browsers already refuse.
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "a.example.com",
            new() { ["a.example.com"] = Expiring(3650, SslPolicyErrors.RemoteCertificateChainErrors) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not one browsers trust");
        result.Description.Should().Contain("the proxy has not been restarted");
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_unhealthy_and_names_USER_HOSTS()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "a.example.com",
            new() { ["a.example.com"] = Expiring(60, SslPolicyErrors.RemoteCertificateNameMismatch) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("does not cover that name");

        // What the proxy does with a name missing from USER_HOSTS, seen against the real one: it
        // completes the handshake with another name's certificate rather than refusing it.
        result.Description.Should().Contain("USER_HOSTS as well as ACME_HOSTS");
    }

    [Fact]
    public async Task A_refused_handshake_is_unhealthy()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "a.example.com",
            new() { ["a.example.com"] = PublicCertificateProbeResult.Refused("Received an unexpected EOF") });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("would not complete a TLS handshake for a.example.com");
    }

    [Fact]
    public async Task An_unreachable_proxy_is_only_degraded_and_says_nothing_about_the_renewal()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "a.example.com;b.example.com",
            new()
            {
                ["a.example.com"] = PublicCertificateProbeResult.Unreachable("Connection refused"),
                ["b.example.com"] = PublicCertificateProbeResult.Unreachable("Connection refused"),
            });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be(
            "The proxy at proxy:8443 could not be reached (Connection refused), so the certificate served for " +
            "a.example.com and b.example.com was not checked.");
    }

    [Fact]
    public async Task The_worst_name_decides_and_each_wrong_one_is_named()
    {
        (PublicCertificateHealthCheck check, _, _) = NewCheck(
            "fine.example.com;soon.example.com;late.example.com",
            new()
            {
                ["fine.example.com"] = Expiring(80),
                ["soon.example.com"] = Expiring(15),
                ["late.example.com"] = Expiring(2),
            });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("soon.example.com expires in 15 days");
        result.Description.Should().Contain("late.example.com expires in 2 days");
        result.Description.Should().NotContain("fine.example.com");
    }

    [Fact]
    public async Task An_entry_that_is_not_a_name_is_unhealthy_and_the_names_beside_it_are_still_checked()
    {
        (PublicCertificateHealthCheck check, TableProbe probe, _) = NewCheck(
            "a.example.com;no_good.example.com",
            new() { ["a.example.com"] = Expiring(60) });

        HealthCheckResult result = await RunAsync(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("\"no_good.example.com\" in ACME_HOSTS is not a name");
        probe.Asked.Should().Equal("a.example.com");
    }

    [Fact]
    public void Hosts_are_read_as_the_renewal_reads_them()
    {
        (IReadOnlyList<string> names, IReadOnlyList<string> unusable) =
            PublicCertificateHealthCheck.ParseHosts(" a.example.com ; ;b.example.com;A.EXAMPLE.COM;.example.com;*");

        names.Should().Equal("a.example.com", "b.example.com");
        unusable.Should().Equal(".example.com", "*");
    }

    [Fact]
    public async Task The_proxy_is_asked_at_most_once_an_hour()
    {
        (PublicCertificateHealthCheck check, TableProbe probe, FakeTimeProvider time) =
            NewCheck("a.example.com", new() { ["a.example.com"] = Expiring(60) });

        await RunAsync(check);
        time.Advance(TimeSpan.FromMinutes(59));
        await RunAsync(check);
        probe.Asked.Should().HaveCount(1);

        time.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(check);
        probe.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unreachable_proxy_is_asked_again_after_a_minute()
    {
        // The proxy starts after the application, so the first probe can miss it; an hour's banner for
        // that would be one nobody could clear.
        (PublicCertificateHealthCheck check, TableProbe probe, FakeTimeProvider time) =
            NewCheck("a.example.com", new() { ["a.example.com"] = PublicCertificateProbeResult.Unreachable("refused") });

        await RunAsync(check);
        time.Advance(TimeSpan.FromSeconds(61));
        await RunAsync(check);

        probe.Asked.Should().HaveCount(2);
    }

    [Fact]
    public void The_check_is_registered_untagged_so_it_reaches_the_anonymous_status_and_never_liveness()
    {
        ServiceCollection services = new();
        services.AddHomespoolHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();

        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
                                                       .Value.Registrations.Single(r => r.Name == "public-certificate");

        registration.Tags.Should().BeEmpty();
    }
}
