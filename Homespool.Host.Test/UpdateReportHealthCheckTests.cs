using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// are the appliance's own, before and after it pulled the first labelled images, and the third is its
/// report from the night before a pull, still in place after it.
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

    private const string ReportedRevision = "12b8c52c21b72e1f01445ee04a9a57b142d88a32";

    private const string PulledRevision = "6a9558eaf13150ed328fd50d830ee46377b3507e";

    private const string AspnetBase = "sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f";

    /// <summary>
    /// Trimmed to the fields the check reads and the two revisions: the other services and the counts
    /// are as the appliance wrote them.
    /// </summary>
    private const string BeforePull = $$"""
        {
          "schema": 1,
          "checked": "2026-09-30T01:43:07Z",
          "update_available": true,
          "services": [
            {
              "service": "homespool",
              "status": "newer",
              "running": { "revision": "{{ReportedRevision}}", "base": "{{AspnetBase}}" },
              "published": { "revision": "346d97c36250ecfe7c024e256ad8e0bcc764c519", "base": "{{AspnetBase}}" },
              "reasons": [ "10 Homespool fixes" ]
            },
            {
              "service": "proxy",
              "status": "newer",
              "running": { "revision": "{{ReportedRevision}}", "base": "sha256:9eab" },
              "published": { "revision": "346d97c36250ecfe7c024e256ad8e0bcc764c519", "base": "sha256:9eab" },
              "reasons": [ "10 Homespool fixes" ]
            },
            {
              "service": "go2rtc",
              "status": "newer",
              "running": { "revision": "{{ReportedRevision}}", "base": "sha256:294b" },
              "published": { "revision": "346d97c36250ecfe7c024e256ad8e0bcc764c519", "base": "sha256:294b" },
              "reasons": [ "10 Homespool fixes" ]
            }
          ]
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 19, 0, 0, TimeSpan.Zero);

    /// <summary>When the appliance was found still showing <see cref="BeforePull"/> after its pull.</summary>
    private static readonly DateTimeOffset AfterPull = new(2026, 9, 30, 20, 31, 0, TimeSpan.Zero);

    private static readonly RunningImage Unknown = new(null, null);

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

    private UpdateReportHealthCheck NewCheck(RunningImage? running = null)
    {
        return new(Options.Create(new UpdateReportOptions { Path = ReportPath }), running ?? Unknown, _time);
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

    /// <summary>
    /// Registered for administrators only, so its Degraded - this deployment is behind a published
    /// fix - never reaches the anonymous status; and not for liveness, since a restart pulls nothing.
    /// </summary>
    [Fact]
    public void The_check_is_for_administrators_only_and_never_decides_liveness()
    {
        ServiceCollection services = new();
        services.AddHomespoolHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();

        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
                                                       .Value.Registrations.Single(r => r.Name == "update-check");

        registration.Tags.Should().BeEquivalentTo([HealthEndpoints.AdministratorsOnlyTag]);
    }

    /// <summary>
    /// The base digest comes from the variable the application image sets, through configuration, as
    /// <c>compose.yaml</c> delivers it.
    /// </summary>
    [Fact]
    public void The_running_base_is_read_from_the_image_s_variable()
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection([new(RunningImage.ImageBaseVariable, $"mcr.microsoft.com/dotnet/aspnet:10.0@{AspnetBase}")])
            .Build());
        services.AddHomespoolHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<RunningImage>().BaseDigest.Should().Be(AspnetBase);
    }

    [Theory]
    [InlineData($"mcr.microsoft.com/dotnet/aspnet:10.0@{AspnetBase}", AspnetBase)]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0", null)]
    [InlineData(null, null)]
    public void The_base_digest_is_what_follows_the_at(string? imageBase, string? expected)
    {
        RunningImage.From(PulledRevision, imageBase).BaseDigest.Should().Be(expected);
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
    /// The appliance's own report from before it pulled: both images newer for the same reason, said
    /// once for both, and the command that acts on it - as plain text, since the banner, the mail and
    /// <c>/health</c> all carry the description as it is.
    /// </summary>
    [Fact]
    public async Task A_newer_image_with_reasons_is_degraded_and_says_what_to_run()
    {
        await WriteAsync(BeforeLabels);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be(
            "Newer Homespool images are published for homespool and proxy: the running image does not say " +
            "which Homespool revision it is. To take them, run docker compose pull && docker compose up -d " +
            "where the stack runs.");
    }

    [Fact]
    public async Task Services_with_different_reasons_are_listed_apart()
    {
        await WriteAsync("""
            {
              "schema": 1,
              "checked": "2026-09-26T18:00:00Z",
              "services": [
                { "service": "homespool", "status": "newer", "reasons": [ "10 Homespool fixes" ] },
                { "service": "proxy", "status": "newer", "reasons": [ "10 Homespool fixes" ] },
                { "service": "go2rtc", "status": "newer", "reasons": [ "10 Homespool fixes", "built on a newer base" ] }
              ]
            }
            """);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Description.Should().StartWith(
            "Newer Homespool images are published for homespool and proxy: 10 Homespool fixes; " +
            "for go2rtc: 10 Homespool fixes, built on a newer base. ");
    }

    [Fact]
    public async Task One_newer_image_is_spoken_of_as_one()
    {
        await WriteAsync(Report("2026-09-26T18:00:00Z"));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Description.Should().Be(
            "A newer Homespool image is published for homespool: 2 Homespool fixes. " +
            "To take it, run docker compose pull && docker compose up -d where the stack runs.");
    }

    [Fact]
    public async Task Three_services_with_the_same_reasons_are_listed_as_a_sentence_would()
    {
        await WriteAsync("""
            {
              "schema": 1,
              "checked": "2026-09-26T18:00:00Z",
              "services": [
                { "service": "homespool", "status": "newer", "reasons": [ "10 Homespool fixes" ] },
                { "service": "proxy", "status": "newer", "reasons": [ "10 Homespool fixes" ] },
                { "service": "go2rtc", "status": "newer", "reasons": [ "10 Homespool fixes" ] }
              ]
            }
            """);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Description.Should().StartWith(
            "Newer Homespool images are published for homespool, proxy and go2rtc: 10 Homespool fixes. ");
    }

    [Fact]
    public async Task No_description_carries_markdown()
    {
        await WriteAsync(BeforeLabels);
        string newer = (await RunAsync(NewCheck())).Description!;

        _time.Advance(TimeSpan.FromDays(4));
        string stale = (await RunAsync(NewCheck())).Description!;

        await WriteAsync("not json at all");
        string unreadable = (await RunAsync(NewCheck())).Description!;

        _time.SetUtcNow(AfterPull);
        await WriteAsync(BeforePull);
        string setAside = (await RunAsync(NewCheck(new RunningImage(PulledRevision, AspnetBase)))).Description!;

        new[] { newer, stale, unreadable, setAside }.Should().AllSatisfy(d => d.Should().NotContain("`"));
    }

    [Fact]
    public async Task A_newer_image_with_nothing_worth_taking_is_healthy()
    {
        await WriteAsync(Report("2026-09-26T18:00:00Z", appReasons: string.Empty));

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("homespool has a newer image published")
              .And.Contain("nothing in it the check counts as a reason");
    }

    /// <summary>
    /// The case the review found: a card builds its images itself, so both are local, and the check
    /// used to fall through to saying they were the ones their registry publishes.
    /// </summary>
    [Fact]
    public async Task Images_built_on_this_machine_are_never_called_current()
    {
        await WriteAsync($$"""
            {
              "schema": 1,
              "checked": "2026-09-26T18:00:00Z",
              "services": [
                { "service": "homespool", "reference": "homespool", "status": "local", "built": "2026-08-14T02:34:59.915970875Z" },
                { "service": "proxy", "reference": "homespool-proxy", "status": "local", "built": "2026-08-14T02:27:14Z" }
              ]
            }
            """);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy, "nothing can be acted on until images are published");
        result.Description.Should().NotContain("registry publishes")
              .And.Contain("homespool was built from source rather than pulled on 2026-08-14")
              .And.Contain("proxy was built from source rather than pulled on 2026-08-14")
              .And.Contain("no check can say whether fixes have come out since");
    }

    [Theory]
    [InlineData("pinned", "\"built\": \"2026-08-14T00:00:00Z\"", "homespool is pinned to a digest on 2026-08-14, so there is no tag to follow")]
    [InlineData("not-running", "\"built\": null", "homespool was not running")]
    [InlineData("local", "\"built\": \"\"", "homespool was built from source rather than pulled, so nothing is published")]
    [InlineData("from-the-future", "\"built\": null", "homespool is 'from-the-future', which this version does not know")]
    public async Task Every_other_status_says_what_it_means(string status, string built, string expected)
    {
        await WriteAsync($$"""
            {
              "schema": 1,
              "checked": "2026-09-26T18:00:00Z",
              "services": [
                { "service": "homespool", "status": "{{status}}", {{built}} },
                { "service": "proxy", "status": "current" }
              ]
            }
            """);

        HealthCheckResult result = await RunAsync(NewCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain(expected)
              .And.Contain("proxy is the image its registry publishes")
              .And.NotContain("The images are the ones their registry publishes");
    }

    /// <summary>
    /// The appliance's case: pulled and recreated in the evening, and the night's report still named
    /// the revision it had replaced, so the banner told whoever had just updated to update.
    /// </summary>
    [Fact]
    public async Task A_report_about_a_replaced_revision_is_set_aside()
    {
        _time.SetUtcNow(AfterPull);
        await WriteAsync(BeforePull);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(PulledRevision, AspnetBase)));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be(
            "The image update check at 2026-09-30 01:43 UTC looked at images that are no longer running, so " +
            "what it found does not apply to these. Its next run on the host will say whether they are the " +
            "newest published.");
    }

    [Fact]
    public async Task The_same_revision_on_another_base_is_a_replaced_image_too()
    {
        _time.SetUtcNow(AfterPull);
        await WriteAsync(BeforePull);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(ReportedRevision, "sha256:0ther")));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("no longer running");
    }

    [Fact]
    public async Task A_report_about_the_running_image_is_repeated()
    {
        _time.SetUtcNow(AfterPull);
        await WriteAsync(BeforePull);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(ReportedRevision, AspnetBase)));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().StartWith(
            "Newer Homespool images are published for homespool, proxy and go2rtc: 10 Homespool fixes. ");
    }

    /// <summary>
    /// Nothing to compare is no difference: a build with no commit stamped, a base built without a
    /// digest, or a report from images that predate the labels still says what the host found.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, AspnetBase)]
    [InlineData(ReportedRevision, null)]
    public async Task Without_both_sides_known_the_report_is_repeated(string? revision, string? baseDigest)
    {
        _time.SetUtcNow(AfterPull);
        await WriteAsync(BeforePull);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(revision, baseDigest)));

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task A_report_whose_images_gave_no_revision_is_repeated()
    {
        await WriteAsync(BeforeLabels);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(PulledRevision, AspnetBase)));

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    /// <summary>A report too old says the check stopped whatever it describes: that is the finding.</summary>
    [Fact]
    public async Task A_stale_report_about_a_replaced_revision_still_says_the_check_has_stopped()
    {
        _time.SetUtcNow(AfterPull.AddDays(4));
        await WriteAsync(BeforePull);

        HealthCheckResult result = await RunAsync(NewCheck(new RunningImage(PulledRevision, AspnetBase)));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("has not reported since");
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
