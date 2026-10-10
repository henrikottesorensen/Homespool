using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Health;

/// <summary>
/// Reports what the host's image update check found: whether a newer image is published that is worth
/// pulling, and whether the check is still running at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not judge; it repeats.</b> The host check decides what makes a newer image worth taking
/// and lists those reasons in its report. This is Degraded exactly when a newer image carries reasons,
/// so the banner and the host's own journal cannot disagree about whether to update.
/// </para>
/// <para>
/// <b>Degraded, never Unhealthy</b>, like <see cref="DeploymentExposureHealthCheck"/>: an update
/// worth taking is not a service that is down, and Unhealthy would make <c>/health</c> answer 503.
/// Untagged, so it never reaches <c>/health/live</c>: a restart pulls nothing.
/// </para>
/// <para>
/// <b>A missing report is not a finding.</b> The volume exists wherever <c>compose.yaml</c> runs; the
/// report exists only where the host check is installed, and a stack without it - on Docker Desktop,
/// say - has nothing to say. A report too old, or one that cannot be read, is a finding: the check was
/// installed and has stopped telling anyone anything.
/// </para>
/// <para>
/// <b>A report about images no longer running says nothing about these.</b> The host checks daily, so
/// after a pull the last report still describes the containers that were replaced, and repeating its
/// reasons would tell whoever just took the update to take it again. When the report names the
/// application's running revision or base and this process was built from another, the report is set
/// aside until the check runs again. The application is the one container whose image this process knows,
/// and it stands for all three because one <c>docker compose up</c> replaces them together. Not always:
/// a container recreated alone leaves the others on another revision, which the report records and the
/// banner names, and the host's watch runs the check again within minutes of any of them changing.
/// </para>
/// <para>
/// <b>Registered as a singleton</b>, because it keeps the last report it read and reads the file again
/// only when the file changes. The health service would otherwise construct it afresh for every
/// report.
/// </para>
/// </remarks>
public sealed class UpdateReportHealthCheck : IHealthCheck
{
    private const int SupportedSchema = 1;

    private const string PullCommand = "docker compose pull && docker compose up -d";

    /// <summary>The application's service name in the report, as <c>compose.yaml</c> names it.</summary>
    private const string ApplicationService = "homespool";

    private readonly IOptions<UpdateReportOptions> _options;
    private readonly RunningImage _running;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _gate = new();

    private DateTime _readWriteTime;
    private long _readLength = -1;
    private Report? _report;
    private string? _readError;

    public UpdateReportHealthCheck(IOptions<UpdateReportOptions> options, RunningImage running,
                                   TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(running);

        _options = options;
        _running = running;
        _timeProvider = timeProvider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                    CancellationToken cancellationToken = default)
    {
        UpdateReportOptions options = _options.Value;
        FileInfo file = new(options.Path);

        if (!file.Exists)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No image update check reports to this deployment. The check runs on the host - " +
                "update-check/install.sh in the repository installs it."));
        }

        (Report? report, string? error) = Read(file);

        if (report is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"The image update check's report at {options.Path} could not be read ({error}), so " +
                "nothing here can say whether a newer image is published. The check on the host writes it; " +
                "journalctl -u homespool-update-check.service there says what it last did."));
        }

        return Task.FromResult(Judge(report, options));
    }

    /// <summary>What a report says, as a health result.</summary>
    /// <param name="report">The report, already read and of a supported schema.</param>
    /// <param name="options">The staleness threshold.</param>
    /// <returns>The result.</returns>
    private HealthCheckResult Judge(Report report, UpdateReportOptions options)
    {
        TimeSpan age = _timeProvider.GetUtcNow() - report.Checked;

        if (age > TimeSpan.FromDays(options.StaleAfterDays))
        {
            return HealthCheckResult.Degraded(
                $"The image update check has not reported since {report.Checked:yyyy-MM-dd HH:mm} UTC, so " +
                "nothing here can say whether a newer image is published. It runs daily on the host from " +
                "homespool-update-check.timer; systemctl status homespool-update-check.timer there says " +
                "whether it is still enabled.");
        }

        string checkedAt = $"{report.Checked:yyyy-MM-dd HH:mm} UTC";

        if (DescribesOtherImages(report))
        {
            return HealthCheckResult.Healthy(
                $"The image update check at {checkedAt} looked at images that are no longer running, so what " +
                "it found does not apply to these. Its next run on the host will say whether they are the " +
                "newest published.");
        }

        List<ReportService> worthPulling =
            [.. report.Services.Where(s => s.Status == "newer" && s.Reasons is { Count: > 0 })];

        if (worthPulling.Count > 0)
        {
            // A line per image, because each counts what it is built from: the same count on two lines
            // is two images' own changes, not one change said twice.
            string lines = string.Join('\n', worthPulling.Select(s => $"{s.Service}: {string.Join(", ", s.Reasons!)}"));
            bool several = worthPulling.Count > 1;

            return HealthCheckResult.Degraded(
                $"{(several ? "Newer Homespool images are" : "A newer Homespool image is")} published:\n{lines}\n" +
                MixedRevisions(report) +
                $"To take {(several ? "them" : "it")}, run {PullCommand} where the stack runs." +
                ComposeChanges(report));
        }

        if (report.Services.All(s => s.Status == "current"))
        {
            return HealthCheckResult.Healthy(
                $"The images are the ones their registry publishes, as of the update check at {checkedAt}.");
        }

        return HealthCheckResult.Healthy(
            $"Image update check at {checkedAt}: {string.Join("; ", report.Services.Select(Describe))}.");
    }

    /// <summary>
    /// Whether the report's application container was running an image other than this process's:
    /// another revision, or the same revision on another base. Unknown on either side is no difference.
    /// </summary>
    private bool DescribesOtherImages(Report report)
    {
        ReportImage? reported = report.Services.FirstOrDefault(s => s.Service == ApplicationService)?.Running;

        return reported is not null &&
               (Differs(reported.Revision, _running.Revision) || Differs(reported.Base, _running.BaseDigest));

        static bool Differs(string? reported, string? own)
        {
            return !string.IsNullOrEmpty(reported) &&
                   !string.IsNullOrEmpty(own) &&
                   !string.Equals(reported, own, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A sentence naming each container's revision, when they do not all run the same one - one
    /// recreated without the others - and nothing when they do. Without it, three images a different
    /// distance behind read as one update counted three ways.
    /// </summary>
    private static string MixedRevisions(Report report)
    {
        List<IGrouping<string, ReportService>> revisions =
        [
            .. report.Services
                     .Where(s => !string.IsNullOrEmpty(s.Running?.Revision))
                     .GroupBy(s => Short(s.Running!.Revision!), StringComparer.Ordinal),
        ];

        if (revisions.Count < 2)
        {
            return string.Empty;
        }

        IEnumerable<string> clauses = revisions.Select(
            g => $"{JoinNames([.. g.Select(s => s.Service)])} {(g.Count() > 1 ? "run" : "runs")} {g.Key}");

        return $"The containers are not all one build: {string.Join(", ", clauses)}. ";
    }

    /// <summary>
    /// A sentence saying <c>compose.yaml</c> changed between the application's running and published
    /// revisions, when it did: no pull brings that, so somebody has to compare the deployment's copy.
    /// </summary>
    private static string ComposeChanges(Report report)
    {
        ReportService? application = report.Services.FirstOrDefault(s => s.Service == ApplicationService);

        if (application?.Compose is not { Commits: > 0 } compose)
        {
            return string.Empty;
        }

        string count = $"{compose.Commits} {(compose.Commits == 1 ? "commit" : "commits")}" +
                       (compose.Found ? string.Empty : " of those read, which do not reach the running revision");
        string? published = application.Published?.Revision;
        string at = string.IsNullOrEmpty(published) ? string.Empty : $" at {Short(published)}";

        return $" compose.yaml changed too, in {count}, and a pull does not bring that: compare the " +
               $"deployment's copy with the repository's{at}.";
    }

    /// <summary>A revision as people quote one: its first eight characters.</summary>
    private static string Short(string revision)
    {
        return revision[..Math.Min(8, revision.Length)];
    }

    /// <summary>Service names as a sentence lists them: "a", "a and b", "a, b and c".</summary>
    private static string JoinNames(IReadOnlyList<string> names)
    {
        return names.Count == 1 ?
            names[0] :
            $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    /// <summary>One container's line, for every status but a newer image worth pulling.</summary>
    /// <remarks>
    /// Each status says what it means, because the one the host check cannot compare is the one a
    /// catch-all would misreport: a card's images are built on the machine that makes the card and baked
    /// into it, never pulled, so they are <c>local</c>, and
    /// "current" would claim the very thing nobody can know. Healthy all the same: nothing here can be
    /// acted on until images are published for the deployment to pull, and a banner nobody can clear
    /// teaches people to stop reading banners.
    /// </remarks>
    /// <param name="service">The container.</param>
    /// <returns>A clause naming it.</returns>
    private static string Describe(ReportService service)
    {
        return service.Status switch
        {
            "current" => $"{service.Service} is the image its registry publishes",
            "restamped" => $"{service.Service} differs from the image its registry publishes only in the commit it is stamped with",
            "newer" => $"{service.Service} has a newer image published, with nothing in it the check counts as a reason to update",
            "local" => $"{service.Service} was built from source rather than pulled{Built(service)}, so nothing is " +
                       "published to compare it with, and no check can say whether fixes have come out since",
            "pinned" => $"{service.Service} is pinned to a digest{Built(service)}, so there is no tag to follow",
            "older" => Older(service),
            "not-running" => $"{service.Service} was not running",
            _ => $"{service.Service} is '{service.Status}', which this version does not know",
        };
    }

    /// <summary>
    /// A container running a newer release than the one its tag now holds, naming both when the report
    /// does. Not an update: pulling would take the deployment back.
    /// </summary>
    private static string Older(ReportService service)
    {
        string? running = service.Running?.Version;
        string? published = service.Published?.Version;

        return string.IsNullOrEmpty(running) || string.IsNullOrEmpty(published) ?
            $"{service.Service} runs a newer release than its registry publishes under its tag, so there is nothing to take" :
            $"{service.Service} runs release {running}, and its registry publishes the older {published} under its tag, " +
            "so there is nothing to take";
    }

    /// <summary>
    /// The build date, when the report carries one that reads as a date. Text in the report rather
    /// than a date, so a value Docker left odd costs the date and not the whole report.
    /// </summary>
    private static string Built(ReportService service)
    {
        return DateTimeOffset.TryParse(service.Built, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                                       out DateTimeOffset built) ?
            $" on {built:yyyy-MM-dd}" :
            string.Empty;
    }

    /// <summary>The report, from the last read unless the file has changed since.</summary>
    /// <param name="file">The report file, known to exist a moment ago.</param>
    /// <returns>The report, or <see langword="null"/> and why not.</returns>
    private (Report? report, string? error) Read(FileInfo file)
    {
        lock (_gate)
        {
            if (file.LastWriteTimeUtc == _readWriteTime && file.Length == _readLength)
            {
                return (_report, _readError);
            }

            _readWriteTime = file.LastWriteTimeUtc;
            _readLength = file.Length;
            (_report, _readError) = Parse(file.FullName);

            return (_report, _readError);
        }
    }

    private static (Report? report, string? error) Parse(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Report? report = JsonSerializer.Deserialize<Report>(stream);

            if (report is null)
            {
                return (null, "the file is empty");
            }

            if (report.Schema != SupportedSchema)
            {
                return (null, $"schema {report.Schema}, where this version reads {SupportedSchema}");
            }

            // Non-nullable in the record, and still null when the key is missing: the serialiser does
            // not enforce the annotation.
            if (report.Services is null)
            {
                return (null, "it lists no services");
            }

            return (report, null);
        }
        catch (JsonException e)
        {
            return (null, $"not the JSON it should be: {e.Message}");
        }
        catch (IOException e)
        {
            return (null, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            return (null, e.Message);
        }
    }

    // CA1812 calls both records never instantiated, which is true at compile time: System.Text.Json
    // only ever builds them by reflection, reading the report.

    /// <summary>The part of the host check's report this reads; the rest is for people.</summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json when reading the report.")]
    private sealed record Report(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("checked")] DateTimeOffset Checked,
        [property: JsonPropertyName("services")] IReadOnlyList<ReportService> Services);

    /// <summary>One container in the report.</summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json when reading the report.")]
    private sealed record ReportService(
        [property: JsonPropertyName("service")] string Service,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("reasons")] IReadOnlyList<string>? Reasons,
        [property: JsonPropertyName("built")] string? Built,
        [property: JsonPropertyName("running")] ReportImage? Running,
        [property: JsonPropertyName("published")] ReportImage? Published,
        [property: JsonPropertyName("compose")] ReportCompose? Compose);

    /// <summary>
    /// The commits that changed <c>compose.yaml</c> between the application's running and published
    /// revisions, and whether the host's walk reached the running one - when it did not, the count is of
    /// the commits it read, and the running revision may be further back or not in that history at all.
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json when reading the report.")]
    private sealed record ReportCompose(
        [property: JsonPropertyName("commits")] int Commits,
        [property: JsonPropertyName("found")] bool Found);

    /// <summary>
    /// An image, running or published, as its labels described it to the host check. The release
    /// version is reported only for an older published release.
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json when reading the report.")]
    private sealed record ReportImage(
        [property: JsonPropertyName("revision")] string? Revision,
        [property: JsonPropertyName("base")] string? Base,
        [property: JsonPropertyName("version")] string? Version);
}
