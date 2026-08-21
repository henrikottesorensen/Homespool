using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterQueryService.GetTemperatureSeriesAsync"/> - the bucketed read behind the printer
/// page's temperature graph.
/// </summary>
/// <remarks>
/// <b>Against real SQLite, and it has to be.</b> The query is raw SQL that groups by integer division
/// of the timestamp column, which is only meaningful because <c>DateTimeOffset</c> is stored as epoch
/// milliseconds in an INTEGER column - and it materialises an unmapped type by column name. None of
/// that is checked by the compiler, and none of it would be exercised by a substitute.
/// </remarks>
public sealed class TemperatureSeriesQueryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-tempseries-{Guid.NewGuid():N}.db");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static PrinterQueryService ServiceFor(HomespoolDbContext context)
    {
        return new PrinterQueryService(context,
                                       new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                       new TeamCapabilityLookup(context),
                                       TimeProvider.System);
    }

    /// <summary>
    /// A printer on the caller's own team, creating that team the first time.
    /// </summary>
    /// <remarks>
    /// One team per user here, because <c>TeamMembers.UserId</c> is unique - a second team for the
    /// same person fails on the constraint rather than on anything this file is about.
    /// </remarks>
    private static async Task<Printer> AddPrinterAsync(HomespoolDbContext context, long userId)
    {
        Team? team = await context.Teams
                                  .Where(t => t.Members.Any(m => m.UserId == userId))
                                  .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        if (team is null)
        {
            team = new Team
            {
                CreatedBy = userId,
                CreatedAt = Start,
                Members = { new TeamMember { UserId = userId, Capabilities = TestMemberships.Graded(true, true, true), IsDefault = true } },
            };

            context.Teams.Add(team);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Status = PrinterStatus.Unknown,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    /// <summary>One sample a second, the rate a real printer reports at.</summary>
    private static async Task AddSamplesAsync(HomespoolDbContext context,
                                              int printerId,
                                              int seconds,
                                              Func<int, (float nozzle, float bed, float targetNozzle, float targetBed)> shape)
    {
        List<TelemetrySample> samples = [];

        for (int second = 0; second < seconds; second++)
        {
            (float nozzle, float bed, float targetNozzle, float targetBed) = shape(second);

            samples.Add(new TelemetrySample
            {
                PrinterId = printerId,
                Timestamp = Start.AddSeconds(second),
                Status = PrinterStatus.Printing,
                NozzleTemperature = nozzle,
                BedTemperature = bed,
                TargetNozzleTemperature = targetNozzle,
                TargetBedTemperature = targetBed,
            });
        }

        context.TelemetrySamples.AddRange(samples);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The query runs, and returns far fewer rows than it read - which is the whole reason it is
    /// SQL rather than a projection bucketed in memory.
    /// </summary>
    [Fact]
    public async Task ThousandsOfSamplesComeBackAsAHandfulOfPoints()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        await AddSamplesAsync(context, printer.Id, seconds: 3600, second => (215, 60, 215, 60));

        TemperatureSeries? series = await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(1), Start, Start.AddHours(1), CancellationToken.None);

        series.Should().NotBeNull();
        series!.Points.Should().NotBeEmpty();
        series.Points.Should().HaveCountLessThan(400, "3 600 rows are summarised, not returned");
    }

    /// <summary>
    /// The buckets carry the shape of the data rather than one figure for the lot - a heat-up has to
    /// still look like a heat-up after it has been summarised.
    /// </summary>
    [Fact]
    public async Task TheBucketsFollowTheReadings()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        // A ramp from 20 to 220 over the hour.
        await AddSamplesAsync(context, printer.Id, seconds: 3600,
                              second => (20 + (second / 18f), 60, 220, 60));

        TemperatureSeries series = (await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(1), Start, Start.AddHours(1), CancellationToken.None))!;

        series.Points[0].Nozzle.Should().BeLessThan(40);
        series.Points[^1].Nozzle.Should().BeGreaterThan(200);

        // Ordered by time, which the graph relies on and nothing else asserts.
        series.Points.Select(point => point.At).Should().BeInAscendingOrder();
    }

    /// <summary>
    /// A setpoint takes the higher of a bucket it changes in, rather than the mean - which would be a
    /// temperature the printer was never asked for.
    /// </summary>
    [Fact]
    public async Task ASetpointIsNotAveragedAcrossAChange()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        // Target steps from 200 to 240 halfway through a two-minute window, so every bucket that
        // spans the change has both values in it.
        await AddSamplesAsync(context, printer.Id, seconds: 120,
                              second => (210, 60, second < 60 ? 200 : 240, 60));

        TemperatureSeries series = (await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(1), Start, Start.AddMinutes(2), CancellationToken.None))!;

        series.Points.Select(point => point.TargetNozzle).Should().AllSatisfy(
            target => target.Should().BeOneOf(200d, 240d));
    }

    /// <summary>
    /// A window the printer said nothing in comes back empty rather than as an error, which is what
    /// the page's "reported no temperatures" line is for.
    /// </summary>
    [Fact]
    public async Task AQuietWindowIsEmptyRatherThanAFailure()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        await AddSamplesAsync(context, printer.Id, seconds: 60, second => (215, 60, 215, 60));

        TemperatureSeries series = (await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(1), Start.AddHours(5), Start.AddHours(6), CancellationToken.None))!;

        series.Points.Should().BeEmpty();
    }

    /// <summary>
    /// Samples outside the window stay outside it. Worth pinning because the bucket key is derived
    /// from the raw timestamp rather than from the window, so an off-by-one here would silently widen
    /// every graph.
    /// </summary>
    [Fact]
    public async Task OnlyTheWindowIsRead()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        await AddSamplesAsync(context, printer.Id, seconds: 600, second => (215, 60, 215, 60));

        TemperatureSeries series = (await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(1), Start.AddMinutes(5), Start.AddMinutes(10), CancellationToken.None))!;

        series.Points.Should().NotBeEmpty();
        series.Points.Select(point => point.At).Should().AllSatisfy(at =>
        {
            at.Should().BeOnOrAfter(Start.AddMinutes(5));
            at.Should().BeOnOrBefore(Start.AddMinutes(10));
        });
    }

    /// <summary>
    /// The same 404 rule the rest of this service follows: a printer the caller cannot view answers
    /// null, so a graph never confirms a uuid belongs to somebody else's team.
    /// </summary>
    [Fact]
    public async Task APrinterTheCallerCannotViewAnswersNull()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1);

        await AddSamplesAsync(context, printer.Id, seconds: 60, second => (215, 60, 215, 60));

        TemperatureSeries? series = await ServiceFor(context).GetTemperatureSeriesAsync(
            printer.Uuid, Caller.Unscoped(2), Start, Start.AddHours(1), CancellationToken.None);

        series.Should().BeNull();
    }

    /// <summary>
    /// One printer's graph shows one printer. The bucket key has no printer in it, so the filter is
    /// the only thing keeping two machines' traces apart.
    /// </summary>
    [Fact]
    public async Task AnotherPrintersSamplesStayOut()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer mine = await AddPrinterAsync(context, userId: 1);
        Printer theirs = await AddPrinterAsync(context, userId: 1);

        await AddSamplesAsync(context, mine.Id, seconds: 120, second => (215, 60, 215, 60));
        await AddSamplesAsync(context, theirs.Id, seconds: 120, second => (60, 20, 60, 20));

        TemperatureSeries series = (await ServiceFor(context).GetTemperatureSeriesAsync(
            mine.Uuid, Caller.Unscoped(1), Start, Start.AddMinutes(2), CancellationToken.None))!;

        series.Points.Select(point => point.Nozzle).Should().AllSatisfy(
            nozzle => nozzle.Should().BeApproximately(215, 0.001));
    }
}
