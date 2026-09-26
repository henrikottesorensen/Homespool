using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Health;

namespace Homespool.Host.Test;

/// <summary>
/// What the administrators' banner says about the host's image update check: whether a newer image is
/// worth pulling, and whether the check has stopped reporting.
/// </summary>
/// <remarks>
/// The descriptions are asserted as well as the statuses, because <c>HealthBanner</c> shows them
/// verbatim. The reports are the shape update-check/homespool-update-check.sh writes; the first two
/// are the appliance's own, before and after it pulled the first labelled images.
/// </remarks>
public sealed class UpdateReportHealthCheckTests : IDisposable
{
    private const string BeforeLabels = """
        {
          "schema": 1,
          "checked": "2026-09-26T18:44:58Z",
          "update_available": true,
          "services": [
            {
              "service": "homespool",
              "reference": "registry.example.net/homespool:latest",
              "status": "newer",
              "digest": "sha256:abdf2261360af9058a7bc59b559c7d32d34ba2862252bca21d2bdae4030c501d",
              "running": { "revision": "", "base": "" },
              "published": { "revision": "59213b0eb220c7bdf67c004e54e1a98c0e325091", "base": "sha256:2d58" },
              "homespool": null,
              "runtime": null,
              "base_changed": false,
              "reasons": [ "the running image does not say which Homespool revision it is" ]
            },
            {
              "service": "proxy",
              "reference": "registry.example.net/homespool-proxy:latest",
              "status": "newer",
              "digest": "sha256:2a3bf33e135b757b01f7199b62a244acb4bb6640b7fcb0b838693dbf7dd02dbb",
              "running": { "revision": "", "base": "" },
              "published": { "revision": "59213b0eb220c7bdf67c004e54e1a98c0e325091", "base": "sha256:0918" },
              "homespool": null,
              "runtime": null,
              "base_changed": false,
              "reasons": [ "the running image does not say which Homespool revision it is" ]
            }
          ]
        }
        """;

    private const string Current = """
        {
          "schema": 1,
          "checked": "2026-09-26T18:50:26Z",
          "update_available": false,
          "services": [
            { "service": "homespool", "reference": "registry.example.net/homespool:latest", "status": "current", "digest": "sha256:abdf" },
            { "service": "proxy", "reference": "registry.example.net/homespool-proxy:latest", "status": "current", "digest": "sha256:2a3b" }
          ]
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 19, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-update-report-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(Now);

    public UpdateReportHealthCheckTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string ReportPath => Path.Combine(_root, "update-check.json");

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private UpdateReportHealthCheck NewCheck()
    {
        return new(Options.Create(new UpdateReportOptions { Path = ReportPath }), _time);
    }

    private static Task<HealthCheckResult> RunAsync(UpdateReportHealthCheck check)
    {
        return check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    private Task WriteAsync(string report)
    {
        return File.WriteAllTextAsync(ReportPath, report, CancellationToken.None);
    }

    /// <summary>
    /// A newer image carrying reasons, in one service and not the other: only the checked time and one
    /// service's list vary between the cases that need it.
    /// </summary>
    private static string Report(string checkedAt, string appStatus = "newer", string appReasons = "\"2 Homespool fixes\"")
    {
        return $$"""
            {
              "schema": 1,
              "checked": "{{checkedAt}}",
              "services": [
                { "service": "homespool", "status": "{{appStatus}}", "reasons": [ {{appReasons}} ] },
                { "service": "proxy", "status": "current" }
              ]
            }
            """;
    }

    [Fact]
    public async Task No_report_is_healthy_and_says_where_the_check_comes_from()
    {
        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("update-check/install.sh");
    }

    [Fact]
    public async Task Images_the_registry_publishes_are_healthy()
    {
        await WriteAsync(Current);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("as of the update check at 2026-09-26 18:50 UTC");
    }

    /// <summary>
    /// The appliance's own report from before it pulled: both images newer, with the host's reason
    /// repeated per service and the command that acts on it.
    /// </summary>
    [Fact]
    public async Task A_newer_image_with_reasons_is_degraded_and_says_what_to_run()
    {
        await WriteAsync(BeforeLabels);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("homespool: the running image does not say which Homespool revision it is")
              .And.Contain("proxy: the running image does not say which Homespool revision it is")
              .And.Contain("docker compose pull && docker compose up -d");
    }

    [Fact]
    public async Task A_newer_image_with_nothing_worth_taking_is_healthy()
    {
        await WriteAsync(Report("2026-09-26T18:00:00Z", appReasons: string.Empty));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("A newer image is published for homespool")
              .And.Contain("nothing in it the update check counts as a reason");
    }

    [Fact]
    public async Task A_report_older_than_three_days_says_the_check_has_stopped()
    {
        await WriteAsync(Report(Now.AddDays(-3).AddMinutes(-1).ToString("O")));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("has not reported since")
              .And.Contain("homespool-update-check.timer")
              .And.NotContain("docker compose pull", "a stale report's reasons are not repeated as current");
    }

    [Fact]
    public async Task A_report_just_under_three_days_old_still_counts()
    {
        await WriteAsync(Report(Now.AddDays(-3).AddMinutes(1).ToString("O")));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Description.Should().Contain("2 Homespool fixes");
    }

    [Fact]
    public async Task A_report_from_the_future_is_not_stale()
    {
        await WriteAsync(Report(Now.AddHours(2).ToString("O"), appStatus: "current", appReasons: string.Empty));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy, "a clock behind the host's is not a stopped check");
    }

    [Theory]
    [InlineData("not json at all", "not the JSON it should be")]
    [InlineData("""{ "schema": 2, "checked": "2026-09-26T18:00:00Z", "services": [] }""", "schema 2")]
    [InlineData("""{ "schema": 1, "checked": "2026-09-26T18:00:00Z" }""", "lists no services")]
    [InlineData("null", "empty")]
    public async Task A_report_that_cannot_be_read_is_degraded_and_says_why(string content, string why)
    {
        await WriteAsync(content);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("could not be read").And.Contain(why);
    }

    [Fact]
    public async Task A_changed_report_is_read_again()
    {
        UpdateReportHealthCheck check = NewCheck();
        await WriteAsync(Current);
        (await RunAsync(check)).Status.Should().Be(HealthStatus.Healthy);

        await WriteAsync(BeforeLabels);
        File.SetLastWriteTimeUtc(ReportPath, DateTime.UtcNow.AddMinutes(1));

        (await RunAsync(check)).Status.Should().Be(HealthStatus.Degraded);
    }

    /// <summary>
    /// The file is read once per change, not once per page: the same size and time are taken as the
    /// same report, which is what the host's replace-whole writing guarantees.
    /// </summary>
    [Fact]
    public async Task An_unchanged_report_is_not_read_again()
    {
        UpdateReportHealthCheck check = NewCheck();
        await WriteAsync(Current);
        DateTime written = File.GetLastWriteTimeUtc(ReportPath);
        (await RunAsync(check)).Status.Should().Be(HealthStatus.Healthy);

        await File.WriteAllTextAsync(ReportPath, new string('x', Current.Length), CancellationToken.None);
        File.SetLastWriteTimeUtc(ReportPath, written);

        (await RunAsync(check)).Status.Should().Be(HealthStatus.Healthy, "the report was not read again");
    }
}
