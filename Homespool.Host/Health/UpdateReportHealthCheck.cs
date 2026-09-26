using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// <b>Registered as a singleton</b>, because it keeps the last report it read and reads the file again
/// only when the file changes. The health service would otherwise construct it afresh for every
/// report.
/// </para>
/// </remarks>
public sealed class UpdateReportHealthCheck : IHealthCheck
{
    private const int SupportedSchema = 1;

    private const string PullCommand = "docker compose pull && docker compose up -d";

    private readonly IOptions<UpdateReportOptions> _options;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _gate = new();

    private DateTime _readWriteTime;
    private long _readLength = -1;
    private Report? _report;
    private string? _readError;

    public UpdateReportHealthCheck(IOptions<UpdateReportOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
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
                "`journalctl -u homespool-update-check.service` there says what it last did."));
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
                "homespool-update-check.timer; `systemctl status homespool-update-check.timer` there says " +
                "whether it is still enabled.");
        }

        List<ReportService> newer = [.. report.Services.Where(s => s.Status == "newer")];
        List<ReportService> worthPulling = [.. newer.Where(s => s.Reasons is { Count: > 0 })];

        if (worthPulling.Count > 0)
        {
            string reasons = string.Join("; ", worthPulling.Select(s => $"{s.Service}: {string.Join(", ", s.Reasons!)}"));

            return HealthCheckResult.Degraded(
                $"A newer Homespool image is published - {reasons}. " +
                $"Pull it where the stack runs with `{PullCommand}`.");
        }

        if (newer.Count > 0)
        {
            return HealthCheckResult.Healthy(
                $"A newer image is published for {string.Join(" and ", newer.Select(s => s.Service))}, with " +
                $"nothing in it the update check counts as a reason to update (checked {report.Checked:yyyy-MM-dd HH:mm} UTC).");
        }

        return HealthCheckResult.Healthy(
            $"The images are the ones their registry publishes, as of the update check at {report.Checked:yyyy-MM-dd HH:mm} UTC.");
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
        [property: JsonPropertyName("reasons")] IReadOnlyList<string>? Reasons);
}
