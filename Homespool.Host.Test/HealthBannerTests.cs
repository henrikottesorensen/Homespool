using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="HealthBanner"/> - which health problems reach an administrator's screen, and how they
/// are dressed.
/// </summary>
public class HealthBannerTests
{
    private static HealthReport Report(params (string name, HealthStatus status, string? description)[] entries)
    {
        return new(entries.ToDictionary(
                e => e.name,
                e => new HealthReportEntry(e.status, e.description, TimeSpan.Zero, exception: null, data: null)),
            TimeSpan.Zero);
    }

    [Fact]
    public void AHealthyReportShowsNothing()
    {
        IReadOnlyList<HealthBannerItem> items = HealthBanner.From(
            Report(("telemetry-persistence", HealthStatus.Healthy, "Telemetry is being persisted.")));

        items.Should().BeEmpty("a healthy service must look exactly as it did before the banner existed");
    }

    [Fact]
    public void ADegradedCheckIsAWarning()
    {
        IReadOnlyList<HealthBannerItem> items = HealthBanner.From(
            Report(("telemetry-persistence", HealthStatus.Degraded, "3 telemetry flush(es) have failed.")));

        items.Should().ContainSingle();
        items[0].CssClass.Should().Be("alert-warning");
        items[0].Message.Should().Be("3 telemetry flush(es) have failed.");
    }

    [Fact]
    public void AnUnhealthyCheckIsShownAsDanger()
    {
        IReadOnlyList<HealthBannerItem> items = HealthBanner.From(
            Report(("telemetry-persistence", HealthStatus.Unhealthy, "900 printer events were discarded.")));

        items.Should().ContainSingle();
        items[0].CssClass.Should().Be("alert-danger");
    }

    /// <summary>
    /// The check's own wording is what reaches the screen, so the banner cannot drift from what
    /// <c>/health</c> reports about the same condition.
    /// </summary>
    [Fact]
    public void OnlyTheUnhealthyEntriesAreShownAndTheirOwnDescriptionsAreUsed()
    {
        IReadOnlyList<HealthBannerItem> items = HealthBanner.From(Report(
            ("telemetry-persistence", HealthStatus.Unhealthy, "Nothing is reaching the database."),
            ("telemetry-writer-alive", HealthStatus.Healthy, "The telemetry drain loop is running.")));

        items.Should().ContainSingle();
        items[0].Message.Should().Be("Nothing is reaching the database.");
    }

    /// <summary>
    /// A check that fails without describing itself still has to say something useful, rather than
    /// rendering an empty alert.
    /// </summary>
    [Fact]
    public void ACheckWithNoDescriptionFallsBackToItsName()
    {
        IReadOnlyList<HealthBannerItem> items = HealthBanner.From(
            Report(("some-future-check", HealthStatus.Unhealthy, null)));

        items.Should().ContainSingle();
        items[0].Message.Should().Contain("some-future-check").And.Contain("Unhealthy");
    }
}
