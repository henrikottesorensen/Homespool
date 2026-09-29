using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Notifications;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// When a filament change coming is news: the countdown crossing five minutes, once per change, and
/// never a countdown that was already under it.
/// </summary>
public sealed class FilamentChangeWatchTests
{
    private const int Printer = 3;

    private static readonly DateTimeOffset At = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly NotificationQueue _queue = new(NullLogger<NotificationQueue>.Instance);

    private static LiveStateSnapshot Countdown(int? seconds)
    {
        return new LiveStateSnapshot(PrinterStatus.Printing, null, null, 42, seconds);
    }

    private List<PrinterHappening> Published()
    {
        List<PrinterHappening> published = [];

        while (_queue.Reader.TryRead(out PrinterHappening? happening))
        {
            published.Add(happening);
        }

        return published;
    }

    private void Tick(FilamentChangeWatch watch, int? before, int? after, int printer = Printer)
    {
        watch.Observed(printer, Countdown(before), Countdown(after), At);
    }

    [Fact]
    public void CrossingFiveMinutesIsAnnouncedOnce()
    {
        FilamentChangeWatch watch = new(_queue);

        Tick(watch, 400, 320);
        Published().Should().BeEmpty("still more than five minutes out");

        Tick(watch, 320, 295);
        Published().Should().ContainSingle().Which.Should().Be(new FilamentChangeSoon(Printer, 295));

        Tick(watch, 295, 200);
        Tick(watch, 200, 30);
        Published().Should().BeEmpty("the same change is not announced again as it comes closer");
    }

    /// <summary>
    /// The estimate wobbles, and a countdown hovering at the line would otherwise cross it every other
    /// message.
    /// </summary>
    [Fact]
    public void ACountdownWobblingAtTheLineIsAnnouncedOnce()
    {
        FilamentChangeWatch watch = new(_queue);

        Tick(watch, 305, 298);
        Tick(watch, 298, 304);
        Tick(watch, 304, 297);
        Tick(watch, 297, 310);
        Tick(watch, 310, 290);

        Published().Should().ContainSingle();
    }

    [Fact]
    public void TheNextChangeIsAnnouncedAfterThisOneHappens()
    {
        FilamentChangeWatch watch = new(_queue);

        Tick(watch, 320, 290);
        Tick(watch, 5, null);
        Tick(watch, null, 3600);
        Tick(watch, 310, 280);

        Published().Should().HaveCount(2);
    }

    /// <summary>
    /// A countdown already under the line is no news: that is a restart mid-countdown, whose "before"
    /// comes from the database.
    /// </summary>
    [Fact]
    public void ACountdownAlreadyUnderTheLineIsNotNews()
    {
        FilamentChangeWatch watch = new(_queue);

        Tick(watch, 250, 245);

        Published().Should().BeEmpty();
    }

    [Fact]
    public void ACountdownAppearingUnderTheLineIsNewsAndOneAppearingFarOutIsNot()
    {
        FilamentChangeWatch watch = new(_queue);

        Tick(watch, null, 21600, printer: 1);
        Tick(watch, null, 120, printer: 2);

        Published().Should().ContainSingle().Which.PrinterId.Should().Be(2);
    }

    [Fact]
    public void MutedPrintersRoundTripAndGarbageIsIgnored()
    {
        Guid first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid second = Guid.Parse("22222222-2222-2222-2222-222222222222");

        string? stored = NotificationMutes.FormatPrinters([second, first, second]);

        stored.Should().Be($"{first} {second}");
        NotificationMutes.ParsePrinters(stored).Should().BeEquivalentTo([first, second]);
        NotificationMutes.FormatPrinters([]).Should().BeNull();
        NotificationMutes.ParsePrinters($"not-a-guid {first}").Should().BeEquivalentTo([first]);
    }
}
